using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Auth;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;

namespace TeamsGateway.Gateway.Apis;

/// <summary>
/// <c>/v1/events</c>：SSE 事件串流。訂閱者連著收即時新訊息，像 Slack Socket Mode。帶 API key。
/// SSE 的 event id 是 <see cref="PublishedEvent.Seq"/>（不是訊息 id——編輯會沿用同一個訊息 id）。
/// 游標在 client（ADR 0006）：事件一律從內部 log 讀游標之後的，bus 只負責叫醒；斷線重連帶 <c>Last-Event-ID</c> 就從那裡接著讀。
/// </summary>
public static class EventsApi
{
    private const int ReplayBatch = 500;

    public static void MapEventsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/events", async (
            HttpContext http,
            GatewayEventBus bus,
            IDbContextFactory<GatewayDbContext> factory,
            ApiKeyService keys,
            CurrentUser user,
            IOptions<GatewayOptions> options,
            IHostApplicationLifetime lifetime,
            CancellationToken ct,
            string? chats = null,
            string? afterMessage = null) =>
        {
            // 程式要關就斷掉連線：不然 Kestrel 會等這條長連線到 ShutdownTimeout（預設 30 秒）才停得下來，
            // Aspire 的停止 / rebuild 等不到就放棄。斷掉走的是跟訂閱者斷線同一條路（ct 取消），訂閱者重連帶 Last-Event-ID 補回
            using var stopping = lifetime.ApplicationStopping.Register(http.Abort);

            var apiKeyId = user.ApiKeyId;
            var heartbeat = TimeSpan.FromSeconds(Math.Max(1, options.Value.SseHeartbeatSeconds));
            var filter = chats?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet();
            bool Wanted(string conversationId) => filter is not { Count: > 0 } || filter.Contains(conversationId);

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            // 游標 = 已處理到的序號。先訂閱再讀 log：讀完之後才寫進去的事件，通知一定收得到
            var cursor = await StartCursorAsync(factory, http.Request.Headers["Last-Event-ID"].ToString(), afterMessage, ct);
            using var subscription = bus.Subscribe();

            // 立刻 flush header（SSE comment），訂閱者一連上就確定連線成立，不用等第一則訊息
            await http.Response.WriteAsync(": ok\n\n", ct);
            await http.Response.Body.FlushAsync(ct);

            var lastBeat = DateTimeOffset.UtcNow;
            while (true)
            {
                // 把 log 讀到最新：連上時的補收、被通知時的即時都是這一步，通知被合併也不會漏
                cursor = await ReplayAsync(http, factory, cursor, Wanted, ct);

                // 等新事件，最多等到下一次心跳
                var untilBeat = lastBeat + heartbeat - DateTimeOffset.UtcNow;
                if (untilBeat > TimeSpan.Zero && await subscription.WaitAsync(untilBeat, ct))
                    continue;

                // 心跳（SSE comment）：沒訊息時中間的 proxy / NAT 不會把連線當閒置砍掉，訂閱者也靠它判斷連線還活著。
                // 順便確認 key 沒被撤銷——長連線只在連上時驗過一次
                if (apiKeyId is { } id && !await keys.IsActiveAsync(id, ct))
                    return;
                await http.Response.WriteAsync(": ping\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
                lastBeat = DateTimeOffset.UtcNow;
            }
        })
        .RequireAuthorization(GatewayRoles.Api)
        .WithSummary("SSE 即時訊息串流")
        .WithDescription("連著收即時新訊息（像 Slack Socket Mode）。`chats` 逗號分隔過濾對話、省略 = 全部。SSE `id` 是事件序號；重連帶 `Last-Event-ID`（最後收到的 `id`）會先補回斷線期間發過的事件；沒有序號時可帶 `afterMessage`（最後處理的 Teams 訊息 id）從那則之後補。閒置時定期送 `: ping` 心跳；API key 被撤銷會斷線。")
        .WithTags("Events");
    }

    /// <summary>
    /// 從哪個序號之後開始送：
    /// 1. 合法的 <c>Last-Event-ID</c>（斷線重連）；
    /// 2. 沒有的話看 <paramref name="afterMessage"/>：訂閱者重啟、序號沒留下來，但知道自己最後轉出去的是哪則訊息
    ///    （例如 relay 從 Slack 讀回來的）→ 從那則第一次發出之後補（之後的編輯也會重送，訂閱者照版本去重）；
    /// 3. 都沒有 / 看不懂 / 比目前最大序號還大（例如舊 client 帶的訊息 id）→ 從現在開始、不補。
    /// </summary>
    internal static async Task<long> StartCursorAsync(
        IDbContextFactory<GatewayDbContext> factory, string? lastEventId, string? afterMessage, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var max = await db.PublishedEvents.MaxAsync(e => (long?)e.Seq, ct) ?? 0;
        if (long.TryParse(lastEventId, out var last) && last >= 0 && last <= max)
            return last;
        if (!string.IsNullOrEmpty(afterMessage)
            && await db.PublishedEvents.Where(e => e.MessageId == afterMessage).MinAsync(e => (long?)e.Seq, ct) is { } first)
            return first;
        return max;
    }

    /// <summary>把 <paramref name="cursor"/> 之後已入庫的事件分批送完，回傳新游標（不想要的對話也算處理過）。</summary>
    private static async Task<long> ReplayAsync(
        HttpContext http, IDbContextFactory<GatewayDbContext> factory, long cursor, Func<string, bool> wanted, CancellationToken ct)
    {
        while (true)
        {
            var batch = await EventLog.ReadAfterAsync(factory, cursor, ReplayBatch, ct);
            foreach (var evt in batch)
            {
                if (wanted(evt.ConversationId))
                    await WriteAsync(http, evt.Seq, evt.Kind, evt.Payload, ct);
                cursor = evt.Seq;
            }
            if (batch.Count < ReplayBatch)
                return cursor;
        }
    }

    private static async Task WriteAsync(HttpContext http, long seq, string kind, string json, CancellationToken ct)
    {
        await http.Response.WriteAsync($"id: {seq}\nevent: {kind}\ndata: {json}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
