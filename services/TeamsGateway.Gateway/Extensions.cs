using TeamsGateway.Gateway.Apis;
using TeamsGateway.Gateway.Auth;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway;

/// <summary>把所有服務註冊與端點對應收在一處，讓 Program.cs 保持極薄（eShop 的 AddApplicationServices pattern）。</summary>
public static class Extensions
{
    public static void AddApplicationServices(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<GatewayOptions>().BindConfiguration("Gateway");

        builder.AddGatewayDatabase();
        builder.AddTeams();

        builder.Services.AddSingleton<ApiKeyService>();

        // 即時推播：Trouter 背景連線 → 行程內匯流排 → SSE
        builder.Services.AddSingleton<GatewayEventBus>();
        builder.Services.AddSingleton<MessageIngestor>();
        builder.Services.AddSingleton<TrouterClient>();
        builder.Services.AddHostedService<TrouterBackgroundService>();
        builder.Services.AddHostedService<WebhookDeliveryService>();

        // 只追訂閱中的對話，並在訂閱 / 啟動時回補歷史（給 Elasticsearch 歷史搜尋留料）
        builder.Services.AddScoped<Backfiller>();
        builder.Services.AddHostedService<StartupBackfillService>();
        builder.Services.AddHostedService<ReconcileService>(); // 慢更新：定期補編輯 / 刪除

        // MCP：讀 / 發是純 function，開成 MCP tool 給任何 MCP client 用（見 Mcp/TeamsTools）
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();
    }

    public static async Task UseApplicationAsync(this WebApplication app)
    {
        await app.Services.MigrateGatewayDatabaseAsync();

        // /mcp 跟 /v1 一樣要 API key（MCP client 以 header 帶）
        app.UseWhen(
            ctx => ctx.Request.Path.StartsWithSegments("/mcp"),
            branch => branch.Use(RequireApiKey));

        app.MapDefaultEndpoints();
        app.MapAdminApi();
        app.MapChatsApi();
        app.MapEventsApi();
        app.MapWebhooksApi();
        app.MapMcp("/mcp");
        app.MapGet("/", () => "teams-gateway");
    }

    private static async Task RequireApiKey(HttpContext ctx, RequestDelegate next)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        var raw = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : ctx.Request.Headers["X-Api-Key"].ToString();
        var keys = ctx.RequestServices.GetRequiredService<ApiKeyService>();
        if (string.IsNullOrEmpty(raw) || await keys.ValidateAsync(raw, ctx.RequestAborted) is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next(ctx);
    }
}
