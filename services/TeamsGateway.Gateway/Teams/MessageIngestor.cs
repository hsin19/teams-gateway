using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;

namespace TeamsGateway.Gateway.Teams;

public enum IngestResult { Inserted, Updated, Unchanged }

/// <summary>
/// 把一則訊息 upsert 進 DB（Trouter 即時、回補、慢更新共用）。
/// 以 Teams 訊息 id 為主鍵；同 id 但 version 更大 = 被編輯 / 刪除 → 更新內容與標記。live 才 publish。
/// publish 的事件跟訊息同一個交易記進 <see cref="PublishedEvent"/>，SSE 斷線補收靠它。
/// </summary>
public sealed class MessageIngestor(IDbContextFactory<GatewayDbContext> factory, GatewayEventBus bus)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Trouter、輪詢、慢更新、回補會同時寫同一則（先查再插 → 主鍵衝突、編輯的 revision 重複）：寫入一次一筆。
    // 量很小，串行化沒有成本；也讓事件依序號順序 publish
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<bool> IsSubscribedAsync(string conversationId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Conversations.AnyAsync(c => c.Id == conversationId && c.Subscribed, ct);
    }

    public async Task<IngestResult> UpsertAsync(MessageDto dto, string rawJson, bool publish, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            return await UpsertCoreAsync(dto, rawJson, publish, ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<IngestResult> UpsertCoreAsync(MessageDto dto, string rawJson, bool publish, CancellationToken ct)
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
            PublishedEvent? inserted = null;
            if (publish)
            {
                var conv = await db.Conversations.FindAsync([dto.ConversationId], ct);
                if (conv is not null)
                {
                    conv.LastMessageId = dto.Id;
                    conv.UpdatedAt = DateTimeOffset.UtcNow;
                }
                inserted = Record(db, dto);
            }
            await db.SaveChangesAsync(ct);
            if (inserted is not null)
                bus.Publish(new GatewayEvent(inserted.Seq, dto));
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
        var updated = publish ? Record(db, dto) : null;
        await db.SaveChangesAsync(ct);
        if (updated is not null)
            bus.Publish(new GatewayEvent(updated.Seq, dto));
        return IngestResult.Updated;
    }

    /// <summary>把要發的事件加進流水帳；SaveChanges 之後 <see cref="PublishedEvent.Seq"/> 才有值。</summary>
    private static PublishedEvent Record(GatewayDbContext db, MessageDto dto)
    {
        var evt = new PublishedEvent
        {
            MessageId = dto.Id,
            ConversationId = dto.ConversationId,
            Payload = JsonSerializer.Serialize(dto, Web),
            PublishedAt = DateTimeOffset.UtcNow,
        };
        db.PublishedEvents.Add(evt);
        return evt;
    }
}
