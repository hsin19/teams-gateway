using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Auth;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.Apis;

/// <summary>
/// <c>/v1/events</c>：SSE 事件串流。訂閱者連著收即時新訊息，像 Slack Socket Mode。帶 API key。
/// 斷線重連帶 <c>Last-Event-ID</c>（= 最後收到的訊息 id）時，先從 DB 補回斷線期間漏掉的，再接即時。
/// </summary>
public static class EventsApi
{
    private const int CatchUpLimit = 500;
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

            // 先訂閱，斷線補收期間的即時訊息才不會漏；補收過的 id 記下來，live 階段去重
            using var subscription = bus.Subscribe();

            // 立刻 flush header（SSE comment），訂閱者一連上就確定連線成立，不用等第一則訊息
            await http.Response.WriteAsync(": ok\n\n", ct);
            await http.Response.Body.FlushAsync(ct);

            var replayed = new HashSet<string>();
            var lastEventId = http.Request.Headers["Last-Event-ID"].ToString();
            if (!string.IsNullOrEmpty(lastEventId))
            {
                foreach (var message in await CatchUpAsync(factory, lastEventId, ct))
                {
                    if (!Wanted(message.ConversationId))
                        continue;
                    replayed.Add(message.Id);
                    await WriteAsync(http, message, ct);
                }
            }

            await foreach (var message in subscription.Reader.ReadAllAsync(ct))
            {
                if (replayed.Contains(message.Id) || !Wanted(message.ConversationId))
                    continue;
                await WriteAsync(http, message, ct);
            }
        })
        .AddEndpointFilter<ApiKeyFilter>()
        .WithSummary("SSE 即時訊息串流")
        .WithDescription("連著收即時新訊息（像 Slack Socket Mode）。`chats` 逗號分隔過濾對話、省略 = 全部；重連帶 `Last-Event-ID`（最後收到的訊息 id）會先補回斷線期間漏掉的。")
        .WithTags("Events");
    }

    internal static async Task<IReadOnlyList<MessageDto>> CatchUpAsync(IDbContextFactory<GatewayDbContext> factory, string lastEventId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var anchor = await db.MessageEvents
            .Where(m => m.Id == lastEventId)
            .Select(m => (DateTimeOffset?)m.ReceivedAt)
            .FirstOrDefaultAsync(ct);
        if (anchor is not { } since)
            return []; // 找不到錨點就不補，直接接即時

        var missed = await db.MessageEvents
            .Where(m => m.ReceivedAt > since)
            .OrderBy(m => m.ReceivedAt)
            .Take(CatchUpLimit)
            .ToListAsync(ct);
        return missed.Select(ToDto).ToList();
    }

    private static MessageDto ToDto(MessageEvent m) => new(
        m.Id, m.ConversationId, m.SenderMri, m.SenderName, m.MessageType, m.Content, m.SequenceId, m.ComposeTime,
        m.Version, m.Edited, m.Deleted, ChatsvcClient.ParseAttachments(m.RawJson));

    private static async Task WriteAsync(HttpContext http, MessageDto message, CancellationToken ct)
    {
        await http.Response.WriteAsync($"id: {message.Id}\n", ct);
        await http.Response.WriteAsync("event: message\n", ct);
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(message, Web)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
