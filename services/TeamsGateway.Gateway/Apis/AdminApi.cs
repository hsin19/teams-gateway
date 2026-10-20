using System.Text.Json;
using TeamsGateway.Gateway.Auth;
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

        return admin;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}

public sealed record CreateApiKeyRequest(string Name);
