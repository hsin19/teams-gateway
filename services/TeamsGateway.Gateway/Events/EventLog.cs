using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Events;

/// <summary>
/// 內部事件 log（<c>PublishedEvents</c>，ADR 0006）。<see cref="PublishedEvent.Seq"/> 就是 offset：
/// SSE 的游標在 client（<c>Last-Event-ID</c>），各 sink 的游標存 DB，都從這裡往後讀。
/// </summary>
public static class EventLog
{
    /// <summary>序號大於 <paramref name="after"/> 的事件，由舊到新，最多 <paramref name="limit"/> 筆。</summary>
    public static async Task<IReadOnlyList<PublishedEvent>> ReadAfterAsync(
        IDbContextFactory<GatewayDbContext> factory, long after, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.PublishedEvents.Where(e => e.Seq > after).OrderBy(e => e.Seq).Take(limit).AsNoTracking().ToListAsync(ct);
    }

    /// <summary>目前最新的序號；log 是空的回 0。</summary>
    public static async Task<long> MaxSeqAsync(IDbContextFactory<GatewayDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.PublishedEvents.MaxAsync(e => (long?)e.Seq, ct) ?? 0;
    }

    /// <summary>
    /// 事件 payload（ADR 0006 的事件契約）：DTO 的欄位，加上 <c>schemaVersion</c> 與 <c>raw</c>（Teams 原始訊息 JSON）。
    /// raw 讓之後用新邏輯回放時拿得到 DTO 沒挑的欄位；只多欄位，照 DTO 解析的 consumer 不受影響。
    /// </summary>
    public static string Payload(GatewayEvent evt, string rawJson)
    {
        var payload = JsonNode.Parse(evt.ToJson())!.AsObject();
        payload["schemaVersion"] = EventSchema.Version;
        payload["raw"] = ParseRaw(rawJson);
        return payload.ToJsonString();
    }

    private static JsonNode? ParseRaw(string rawJson)
    {
        try
        {
            return JsonNode.Parse(rawJson);
        }
        catch (JsonException)
        {
            return null; // 不是合法 JSON 就不帶，事件照發
        }
    }
}
