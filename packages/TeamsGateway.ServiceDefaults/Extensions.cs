using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Aspire 的共用服務：service discovery、resilience、health check、OpenTelemetry。每個服務專案都要參照這個專案。
// 詳見 https://aka.ms/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string VersionEndpointPath = "/version";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // 預設開 resilience，但不重試 POST 這類非冪等請求：逾時 / 5xx 時對方可能其實收到了，重送會讓 Teams 發話、Slack 貼文、webhook 重複
            http.AddStandardResilienceHandler(options => options.Retry.DisableForUnsafeHttpMethods());

            http.AddServiceDiscovery();
        });

        // 要限制 service discovery 允許的 scheme 就打開下面這段
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            // 每筆 trace / metric / log 都帶版本，換版前後的行為在 Grafana 上分得開
            .ConfigureResource(resource => resource.AddAttributes([new("service.version", AppVersion.Value)]))
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // health check 的請求不記 trace
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // 要記 gRPC 就打開下一行（要裝 OpenTelemetry.Instrumentation.GrpcNetClient）
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // 要送 Azure Monitor 就打開下面這段（要裝 Azure.Monitor.OpenTelemetry.AspNetCore）
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // 預設的 liveness 檢查：app 還有回應就算活著
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Aspire 範本只在 Development 開，因為自訂 response writer 可能洩漏細節（https://aka.ms/aspire/healthchecks）。
        // 這裡用預設 writer，只回 Healthy / Unhealthy，沒有細節，所以正式環境也開——不然 container healthcheck 只能打 /，
        // 註冊的 DB 等檢查形同虛設。

        // /health：所有檢查都過，才算啟動完、可以接流量
        app.MapHealthChecks(HealthEndpointPath);

        // /alive：只看標了 live 的檢查，判斷 app 還活著
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        // 純文字回版本（例如 0.3.0+33be044），不用帶 key：部署端、E2E 拿來確認現在跑的是哪一版
        // 用 RequestDelegate 版的 MapGet：library 沒有 request delegate generator，lambda 版在 AOT（relay）下不安全
        app.MapGet(VersionEndpointPath, context => context.Response.WriteAsync(AppVersion.Value)).ExcludeFromDescription();

        return app;
    }
}
