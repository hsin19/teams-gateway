using Microsoft.OpenApi;
using Scalar.AspNetCore;
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

        // 自訂 metrics（推播可靠度），跟 ServiceDefaults 的 OpenTelemetry 一起匯出
        builder.Services.AddSingleton<GatewayMetrics>();
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(GatewayMetrics.MeterName));

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
        builder.Services.AddHostedService<PollService>();      // 輪詢保險：補 Trouter 漏推的新訊息

        // MCP：讀 / 發是純 function，開成 MCP tool 給任何 MCP client 用（見 Mcp/TeamsTools）
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();

        // 內建 OpenAPI + Scalar UI（/scalar）
        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, context, ct) =>
            {
                document.Info.Title = "teams-gateway";
                document.Info.Version = "v1";
                document.Info.Description = "個人版 Teams 的 HTTP gateway。/v1 給訂閱者（Bearer tgw_…）、/admin 帶 X-Admin-Token。";

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description = "訂閱者 API key：Authorization: Bearer tgw_…",
                };
                document.Components.SecuritySchemes["AdminToken"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = "X-Admin-Token",
                    Description = "管理端點用的 token",
                };

                // 每個端點依路徑套對應 auth，Scalar 的 try-it 就會帶，不用手動貼 header
                var apiKey = new OpenApiSecuritySchemeReference("ApiKey", document);
                var adminToken = new OpenApiSecuritySchemeReference("AdminToken", document);
                foreach (var (path, item) in document.Paths)
                {
                    if (item.Operations is null)
                        continue;
                    var scheme = path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) ? adminToken : apiKey;
                    foreach (var operation in item.Operations.Values)
                        operation.Security = [new OpenApiSecurityRequirement { [scheme] = [] }];
                }
                return Task.CompletedTask;
            });
        });
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
        app.MapGet("/", () => "teams-gateway").ExcludeFromDescription(); // liveness，不進 API doc

        // API doc：/openapi/v1.json + Scalar UI /scalar（借 eShop：關掉預設字型下載，不打外部）
        app.MapOpenApi();
        app.MapScalarApiReference(options => options.DefaultFonts = false);
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
