using Microsoft.Extensions.Options;
using TeamsGateway.Gateway.Auth;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.Apis;

/// <summary><c>/v1</c>：給訂閱者的 REST。帶 API key（<c>Authorization: Bearer tgw_...</c>）。</summary>
public static class ChatsApi
{
    public static RouteGroupBuilder MapChatsApi(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1").AddEndpointFilter<ApiKeyFilter>();

        v1.MapGet("/chats", async (ChatsvcClient chatsvc, CancellationToken ct) =>
            Results.Ok(await chatsvc.GetConversationsAsync(ct)));

        v1.MapGet("/chats/{id}/messages", async (string id, ChatsvcClient chatsvc, CancellationToken ct, int? limit = null, string? cursor = null) =>
        {
            var page = await chatsvc.GetMessagesAsync(id, Math.Clamp(limit ?? 30, 1, 200), cursor, ct);
            return Results.Ok(page);
        });

        v1.MapPost("/chats/{id}/messages", async (string id, SendMessageRequest body, ChatsvcClient chatsvc, IOptions<GatewayOptions> options, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Text))
                return Results.BadRequest(new { error = "text 必填" });
            var result = await chatsvc.SendMessageAsync(id, body.Text, options.Value.DisplayName, ct);
            return Results.Ok(result);
        });

        return v1;
    }
}
