using System.Text.Json;
using TeamsGateway.Service.Auth;
using TeamsGateway.Service.Events;

namespace TeamsGateway.Service.Apis;

/// <summary><c>/v1/events</c>：SSE 事件串流。訂閱者連著收即時新訊息，像 Slack Socket Mode。帶 API key。</summary>
public static class EventsApi
{
    public static void MapEventsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/events", async (HttpContext http, GatewayEventBus bus, CancellationToken ct, string? chats = null) =>
        {
            var filter = chats?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet();

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            using var subscription = bus.Subscribe();
            await foreach (var message in subscription.Reader.ReadAllAsync(ct))
            {
                if (filter is { Count: > 0 } && !filter.Contains(message.ConversationId))
                    continue;

                await http.Response.WriteAsync($"id: {message.Id}\n", ct);
                await http.Response.WriteAsync("event: message\n", ct);
                await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(message, Web)}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
        })
        .AddEndpointFilter<ApiKeyFilter>();
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
