using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using ModelContextProtocol;
using TeamsGateway.Contracts;
using Scalar.AspNetCore;
using TeamsGateway.Gateway.Apis;
using TeamsGateway.Gateway.Auth;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;
using TeamsGateway.Gateway.Mcp;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway;

/// <summary>把所有服務註冊與端點對應收在一處，讓 Program.cs 保持極薄（eShop 的 AddApplicationServices pattern）。</summary>
public static class Extensions
{
    public static void AddApplicationServices(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<GatewayOptions>().BindConfiguration("Gateway");

        // minimal API 的 request / response JSON 走 source-gen：wire DTO（Contracts）+ gateway 自己的型別
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(1, GatewayJsonContext.Default);
        });
        // TypedResults.Problem 的 ProblemDetails：AddProblemDetails 才會把框架自己的 source-gen context 串進上面那條鏈
        builder.Services.AddProblemDetails();

        builder.AddGatewayDatabase();
        builder.AddTeams();

        builder.Services.AddSingleton<ApiKeyService>();

        builder.AddGatewayAuth();

        // 自訂 metrics（推播可靠度），跟 ServiceDefaults 的 OpenTelemetry 一起匯出
        builder.Services.AddSingleton<GatewayMetrics>();
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(GatewayMetrics.MeterName));

        // 收訊息 → 寫進內部事件 log（PublishedEvents）→ bus 通知「有新的」→ SSE / 各 sink 從自己的游標讀（ADR 0006）
        builder.Services.AddSingleton<GatewayEventBus>();
        builder.Services.AddSingleton<MessageIngestor>();
        builder.Services.AddSingleton<TrouterClient>();
        builder.Services.AddHostedService<TrouterBackgroundService>();

        // 事件出口：Gateway:Sinks:<名稱>:Enabled 可關（沒設 = 開）
        var gatewayOptions = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
        if (gatewayOptions.IsSinkEnabled(WebhookSink.SinkName))
        {
            builder.Services.AddSingleton<WebhookSink>();
            builder.Services.AddSingleton<IEventSink>(sp => sp.GetRequiredService<WebhookSink>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookSink>());
        }
        builder.Services.AddSingleton<WebhookSecretProtector>();
        // webhook 目標是訂閱者自己填的：連線時檢查 IP（擋內網 / metadata）、不走 proxy（不然檢查到的是 proxy）、不跟 redirect。
        // 失敗重送由 WebhookSink 自己管：拿掉預設的 resilience——它的 circuit breaker 是整個 named client 共用，
        // 一個掛掉的目標會連累其他目標
        var webhookHttp = builder.Services.AddHttpClient(WebhookSink.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(30));
#pragma warning disable EXTEXP0001 // 實驗性 API；要拿掉 ConfigureHttpClientDefaults 加的 handler 只有它
        webhookHttp.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        webhookHttp.ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var allowPrivate = sp.GetRequiredService<IOptions<GatewayOptions>>().Value.WebhookAllowPrivateNetworks;
                return new SocketsHttpHandler
                {
                    UseProxy = false,
                    AllowAutoRedirect = false,
                    ConnectCallback = (context, ct) => WebhookTargetGuard.ConnectAsync(context, allowPrivate, ct),
                };
            });
        builder.Services.AddHostedService<PublishedEventPruner>();

        // 只追訂閱中的對話，並在訂閱 / 啟動時回補歷史（給 Elasticsearch 歷史搜尋留料）
        builder.Services.AddScoped<Backfiller>();
        builder.Services.AddHostedService<StartupBackfillService>();
        builder.Services.AddHostedService<ReconcileService>(); // 慢更新：定期補編輯 / 刪除
        builder.Services.AddHostedService<PollService>();      // 輪詢保險：補 Trouter 漏推的新訊息

        // MCP：讀 / 發是純 function，開成 MCP tool 給任何 MCP client 用（見 Mcp/TeamsTools）。
        // 明確列出 tool 型別，不掃 assembly（WithToolsFromAssembly 靠 reflection，trim / AOT 下拿不到）
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<TeamsTools>(McpToolJson);

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

    /// <summary>MCP tool 的 JSON：回傳的 contract 型別走 Contracts 的 source-gen，其餘（參數、MCP 協定本身）照 SDK 預設。</summary>
    private static readonly JsonSerializerOptions McpToolJson = new(McpJsonUtilities.DefaultOptions)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(ContractsJsonContext.Default, McpJsonUtilities.DefaultOptions.TypeInfoResolver),
    };

    public static async Task UseApplicationAsync(this WebApplication app)
    {
        await app.Services.MigrateGatewayDatabaseAsync();

        app.UseGatewayAuth();

        app.MapDefaultEndpoints();
        app.MapAdminApi();
        app.MapChatsApi();
        app.MapEventsApi();
        app.MapWebhooksApi();
        app.MapMcp("/mcp").RequireAuthorization(GatewayRoles.Api);
        // 根目錄轉到 API doc，Aspire dashboard 上 gateway 的連結點進去就是文件；liveness 用 /alive、/health
        app.MapGet("/", () => TypedResults.Redirect("/scalar")).ExcludeFromDescription();

        // API doc：/openapi/v1.json + Scalar UI /scalar（借 eShop：關掉預設字型下載，不打外部）
        app.MapOpenApi();
        app.MapScalarApiReference(options => options.DefaultFonts = false);
    }
}
