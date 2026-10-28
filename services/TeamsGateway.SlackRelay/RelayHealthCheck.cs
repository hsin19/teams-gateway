using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// relay 的 <c>/health</c>：真的在轉送才算健康——設定齊全（不齊的話 worker 不啟動），而且連著事件來源。
/// 開了發話（ADR 0012）時，Socket Mode 也要連著：relay 的工作包含發話，斷了要看得出來。
/// 剛啟動（從 Slack 回補對應時還沒連）、斷線重連的空檔都回 Unhealthy；要等多久才算出事由探測的那端決定。
/// 不看 Slack Web API：一時不通由單則重試處理，不代表 relay 壞了。
/// </summary>
public sealed class RelayHealthCheck(IEventSource source, ISlackSocket socket, IOptions<RelayOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var slack = options.Value.Slack;
        // 路由不完整的警告 worker 啟動時記過了，這裡每次探測都算一次，不再記
        var routes = RelayWorker.BuildRoutes(slack.Routes, NullLogger.Instance);
        var result = !RelayWorker.HasDestination(slack, routes)
            ? HealthCheckResult.Unhealthy("Relay:Slack:BotToken or a channel is not configured; the relay is not relaying")
            : !source.Connected
                ? HealthCheckResult.Unhealthy("Not connected to the event source (gateway)")
                : !string.IsNullOrWhiteSpace(slack.AppToken) && !socket.Connected
                    ? HealthCheckResult.Unhealthy("Not connected to Slack Socket Mode; sending from Slack to Teams is down")
                    : HealthCheckResult.Healthy();
        return Task.FromResult(result);
    }
}
