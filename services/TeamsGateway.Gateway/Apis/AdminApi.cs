using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeamsGateway.Gateway.Auth;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.Apis;

/// <summary><c>/admin</c>：綁定帳號、管理 API key。帶 <c>X-Admin-Token</c>。</summary>
public static class AdminApi
{
    public static RouteGroupBuilder MapAdminApi(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").AddEndpointFilter<AdminTokenFilter>();

        // 第一次綁定：把瀏覽器匯出的登入 cookie（spike/http_refresh.py export 的格式）PUT 進來
        admin.MapPut("/login-cookies", async (HttpRequest request, SecureStore store, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync(ct);
            List<LoginCookie>? cookies;
            try
            {
                cookies = JsonSerializer.Deserialize<List<LoginCookie>>(json, Web);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "不是合法的 cookie JSON 陣列" });
            }
            if (cookies is not { Count: > 0 })
                return Results.BadRequest(new { error = "cookie 陣列是空的" });

            await store.SetAsync(TeamsAuthConstants.CookieSecureKey, json, ct);
            return Results.Ok(new { imported = cookies.Count });
        });

        admin.MapGet("/auth", async (SkypeTokenProvider tokens, SecureStore store, CancellationToken ct) =>
        {
            var hasCookies = await store.GetAsync(TeamsAuthConstants.CookieSecureKey, ct) is not null;
            var current = tokens.Current;
            return Results.Ok(new { hasCookies, skypeId = current?.SkypeId, tokenExpiresAt = current?.ExpiresAt });
        });

        admin.MapPost("/auth/refresh", async (SkypeTokenProvider tokens, CancellationToken ct) =>
        {
            var token = await tokens.GetAsync(ct);
            return Results.Ok(new { token.SkypeId, token.ExpiresAt });
        });

        admin.MapPost("/api-keys", async (CreateApiKeyRequest body, ApiKeyService keys, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "name 必填" });
            var (key, raw) = await keys.CreateAsync(body.Name, ct);
            // 明文只回這一次
            return Results.Ok(new { key.Id, key.Name, apiKey = raw });
        });

        admin.MapGet("/api-keys", async (ApiKeyService keys, CancellationToken ct) =>
        {
            var all = await keys.ListAsync(ct);
            return Results.Ok(all.Select(k => new { k.Id, k.Name, k.CreatedAt, k.RevokedAt }));
        });

        admin.MapDelete("/api-keys/{id:guid}", async (Guid id, ApiKeyService keys, CancellationToken ct) =>
            await keys.RevokeAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // 訂閱對話：gateway 只追 / 存訂閱中的對話（Trouter 會推帳號所有對話，但只有這些入庫）
        admin.MapGet("/subscriptions", async (IDbContextFactory<GatewayDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var subs = await db.Conversations.Where(c => c.Subscribed)
                .Select(c => new { c.Id, c.Topic, c.LastMessageId, c.UpdatedAt }).ToListAsync(ct);
            return Results.Ok(subs);
        });

        // chat 可給 id 或群名；訂閱後同步回補歷史（預設一週）
        admin.MapPut("/subscriptions", async (SubscribeRequest body, ChatsvcClient chatsvc, IDbContextFactory<GatewayDbContext> factory, Backfiller backfiller, CancellationToken ct) =>
        {
            var chats = await chatsvc.GetConversationsAsync(ct);
            var hit = ChatsvcClient.IsThreadId(body.Chat)
                ? chats.FirstOrDefault(c => c.Id == body.Chat) ?? new Contracts.ConversationDto(body.Chat, null, null, null)
                : ChatsvcClient.MatchByName(chats, body.Chat);
            if (hit is null)
                return Results.NotFound(new { error = $"找不到對話 '{body.Chat}'" });

            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                var conv = await db.Conversations.FindAsync([hit.Id], ct);
                if (conv is null)
                    db.Conversations.Add(new Conversation { Id = hit.Id, Topic = hit.Topic, Subscribed = true, UpdatedAt = DateTimeOffset.UtcNow });
                else
                {
                    conv.Subscribed = true;
                    conv.Topic = hit.Topic ?? conv.Topic;
                    conv.UpdatedAt = DateTimeOffset.UtcNow;
                }
                await db.SaveChangesAsync(ct);
            }

            await backfiller.BackfillAsync(hit.Id, ct); // 回補歷史
            return Results.Ok(new { id = hit.Id, topic = hit.Topic });
        });

        admin.MapDelete("/subscriptions/{id}", async (string id, IDbContextFactory<GatewayDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var conv = await db.Conversations.FindAsync([id], ct);
            if (conv is null || !conv.Subscribed)
                return Results.NotFound();
            conv.Subscribed = false; // 保留已存的歷史
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return admin;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}

public sealed record CreateApiKeyRequest(string Name);

public sealed record SubscribeRequest(string Chat);
