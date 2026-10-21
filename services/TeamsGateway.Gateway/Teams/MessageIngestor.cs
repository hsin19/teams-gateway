using Microsoft.EntityFrameworkCore;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;

namespace TeamsGateway.Gateway.Teams;

/// <summary>把一則訊息去重寫進 DB（Trouter 即時與回補共用）。live 才 publish 給 SSE / webhook；回補的歷史不 publish。</summary>
public sealed class MessageIngestor(IDbContextFactory<GatewayDbContext> factory, GatewayEventBus bus)
{
    public async Task<bool> IsSubscribedAsync(string conversationId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Conversations.AnyAsync(c => c.Id == conversationId && c.Subscribed, ct);
    }

    /// <summary>新訊息就寫入並回 true；已存在回 false。<paramref name="publish"/> 時才送上匯流排、並更新對話游標。</summary>
    public async Task<bool> StoreAsync(MessageDto dto, string rawJson, bool publish, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.MessageEvents.AnyAsync(m => m.Id == dto.Id, ct))
            return false;

        db.MessageEvents.Add(new MessageEvent
        {
            Id = dto.Id,
            ConversationId = dto.ConversationId,
            SenderMri = dto.From,
            SenderName = dto.FromName,
            MessageType = dto.Type,
            Content = dto.Content,
            SequenceId = dto.SequenceId,
            ComposeTime = dto.Time,
            ReceivedAt = DateTimeOffset.UtcNow,
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
        return true;
    }
}
