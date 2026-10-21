using Microsoft.EntityFrameworkCore;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;

namespace TeamsGateway.Gateway.Teams;

public enum IngestResult { Inserted, Updated, Unchanged }

/// <summary>
/// 把一則訊息 upsert 進 DB（Trouter 即時、回補、慢更新共用）。
/// 以 Teams 訊息 id 為主鍵；同 id 但 version 更大 = 被編輯 / 刪除 → 更新內容與標記。live 才 publish。
/// </summary>
public sealed class MessageIngestor(IDbContextFactory<GatewayDbContext> factory, GatewayEventBus bus)
{
    public async Task<bool> IsSubscribedAsync(string conversationId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Conversations.AnyAsync(c => c.Id == conversationId && c.Subscribed, ct);
    }

    public async Task<IngestResult> UpsertAsync(MessageDto dto, string rawJson, bool publish, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.MessageEvents.FindAsync([dto.Id], ct);

        if (existing is null)
        {
            db.MessageEvents.Add(new MessageEvent
            {
                Id = dto.Id,
                ConversationId = dto.ConversationId,
                SenderMri = dto.From,
                SenderName = dto.FromName,
                MessageType = dto.Type,
                Content = dto.Content,
                SequenceId = dto.SequenceId,
                Version = dto.Version,
                Edited = dto.Edited,
                Deleted = dto.Deleted,
                ComposeTime = dto.Time,
                ReceivedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                RawJson = rawJson,
            });
            if (publish)
            {
                var conv = await db.Conversations.FindAsync([dto.ConversationId], ct);
                if (conv is not null)
                {
                    conv.LastMessageId = dto.Id;
                    conv.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            await db.SaveChangesAsync(ct);
            if (publish)
                bus.Publish(dto);
            return IngestResult.Inserted;
        }

        if (dto.Version <= existing.Version)
            return IngestResult.Unchanged; // 同版或更舊（重送）

        // 被編輯 / 刪除：先留存舊版本，再更新成最新
        db.MessageRevisions.Add(new MessageRevision
        {
            MessageId = existing.Id,
            Version = existing.Version,
            Content = existing.Content,
            RawJson = existing.RawJson,
            SupersededAt = DateTimeOffset.UtcNow,
        });
        existing.Content = dto.Content;
        existing.MessageType = dto.Type;
        existing.Version = dto.Version;
        existing.Edited = dto.Edited;
        existing.Deleted = dto.Deleted;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        existing.RawJson = rawJson;
        await db.SaveChangesAsync(ct);
        if (publish)
            bus.Publish(dto);
        return IngestResult.Updated;
    }
}
