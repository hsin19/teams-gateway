using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace TeamsGateway.Service.Teams;

public static class TeamsExtensions
{
    public static IHostApplicationBuilder AddTeams(this IHostApplicationBuilder builder)
    {
        var dataDir = builder.Configuration["Gateway:DataDir"]
            ?? Path.Combine(builder.Environment.ContentRootPath, ".data");

        // DataProtection 的 key 存檔，重啟後才解得開存下來的 cookie（key 掉了就得重新匯入 cookie）
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
            .SetApplicationName("teams-gateway");

        builder.Services.AddSingleton<SecureStore>();
        builder.Services.AddSingleton<SkypeTokenProvider>();
        return builder;
    }

    public static void MapTeamsAdmin(this WebApplication app)
    {
        // 匯入 cookie、換 token 這類操作要帶 Gateway:AdminToken；沒設定就一律拒絕
        var admin = app.MapGroup("/admin")
            .AddEndpointFilter(new AdminTokenFilter(app.Configuration["Gateway:AdminToken"]));

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
            return Results.Ok(new
            {
                hasCookies,
                skypeId = current?.SkypeId,
                tokenExpiresAt = current?.ExpiresAt,
            });
        });

        // 強制換一次 token，驗證 cookie 還有效
        admin.MapPost("/auth/refresh", async (SkypeTokenProvider tokens, CancellationToken ct) =>
        {
            var token = await tokens.GetAsync(ct);
            return Results.Ok(new { token.SkypeId, token.ExpiresAt });
        });
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
