using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeamsGateway.Gateway.Auth;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;

namespace TeamsGateway.Gateway.Apis;

/// <summary>
/// <c>/v1/events</c>：SSE 事件串流。訂閱者連著收即時新訊息，像 Slack Socket Mode。帶 API key。
/// SSE 的 event id 是 <see cref="PublishedEvent.Seq"/>（不是訊息 id——編輯會沿用同一個訊息 id）。
/// 斷線重連帶 <c>Last-Event-ID</c> 時，先從 DB 補回這個序號之後發過的事件，再接即時。
/// </summary>
public static class EventsApi
{
    private const int ReplayBatch = 500;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapEventsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/events", async (
            HttpContext http,
            GatewayEventBus bus,
            IDbContextFactory<GatewayDbContext> factory,
            CancellationToken ct,
            string? chats = null) =>
        {
            var filter = chats?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet();
            bool Wanted(string conversationId) => filter is not { Count: > 0 } || filter.Contains(conversationId);

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            // 游標 = 已處理到的序號。先讀游標再訂閱：中間發的事件會在下面的補收從 DB 撈到，不會漏
            var cursor = await StartCursorAsync(factory, http.Request.Headers["Last-Event-ID"].ToString(), ct);
            using var subscription = bus.Subscribe();

            // 立刻 flush header（SSE comment），訂閱者一連上就確定連線成立，不用等第一則訊息
            await http.Response.WriteAsync(": ok\n\n", ct);
            await http.Response.Body.FlushAsync(ct);

            cursor = await ReplayAsync(http, factory, cursor, upTo: null, Wanted, ct);

            await foreach (var evt in subscription.Reader.ReadAllAsync(ct))
            {
                if (evt.Seq <= cursor)
                    continue; // 補收時送過了
                // 序號斷洞：匯流排滿了丟掉舊的、或兩個寫入交錯 publish。洞裡的都已入庫，從 DB 補
                if (evt.Seq > cursor + 1)
                    cursor = await ReplayAsync(http, factory, cursor, upTo: evt.Seq - 1, Wanted, ct);
                if (Wanted(evt.Message.ConversationId))
                    await WriteAsync(http, evt.Seq, JsonSerializer.Serialize(evt.Message, Web), ct);
                cursor = evt.Seq;
            }
        })
        .AddEndpointFilter<ApiKeyFilter>()
        .WithSummary("SSE 即時訊息串流")
        .WithDescription("連著收即時新訊息（像 Slack Socket Mode）。`chats` 逗號分隔過濾對話、省略 = 全部。SSE `id` 是事件序號；重連帶 `Last-Event-ID`（最後收到的 `id`）會先補回斷線期間發過的事件。")
        .WithTags("Events");
    }

    /// <summary>
    /// 從哪個序號之後開始送。沒帶 / 看不懂 / 比目前最大序號還大（例如舊 client 帶的訊息 id）→ 從現在開始、不補。
    /// </summary>
    internal static async Task<long> StartCursorAsync(IDbContextFactory<GatewayDbContext> factory, string? lastEventId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var max = await db.PublishedEvents.MaxAsync(e => (long?)e.Seq, ct) ?? 0;
        return long.TryParse(lastEventId, out var last) && last >= 0 && last <= max ? last : max;
    }

    /// <summary>序號在 (<paramref name="after"/>, <paramref name="upTo"/>] 的事件，由舊到新，最多 <paramref name="limit"/> 筆。</summary>
    internal static async Task<IReadOnlyList<PublishedEvent>> ReadAfterAsync(
        IDbContextFactory<GatewayDbContext> factory, long after, long? upTo, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.PublishedEvents.Where(e => e.Seq > after);
        if (upTo is { } max)
            query = query.Where(e => e.Seq <= max);
        return await query.OrderBy(e => e.Seq).Take(limit).AsNoTracking().ToListAsync(ct);
    }

    /// <summary>把 <paramref name="cursor"/> 之後（到 <paramref name="upTo"/> 為止）已入庫的事件分批送完，回傳新游標。</summary>
    private static async Task<long> ReplayAsync(
        HttpContext http, IDbContextFactory<GatewayDbContext> factory, long cursor, long? upTo, Func<string, bool> wanted, CancellationToken ct)
    {
        while (true)
        {
            var batch = await ReadAfterAsync(factory, cursor, upTo, ReplayBatch, ct);
            foreach (var evt in batch)
            {
                if (wanted(evt.ConversationId))
                    await WriteAsync(http, evt.Seq, evt.Payload, ct);
                cursor = evt.Seq;
            }
            if (batch.Count < ReplayBatch)
                return upTo is { } max ? Math.Max(cursor, max) : cursor;
        }
    }

    private static async Task WriteAsync(HttpContext http, long seq, string json, CancellationToken ct)
    {
        await http.Response.WriteAsync($"id: {seq}\nevent: message\ndata: {json}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
