using System.Net.Http.Json;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay;

/// <summary>查 gateway 的 <c>/v1/status</c>（Teams 認證健康）。</summary>
public sealed class GatewayStatusClient(HttpClient http, ILogger<GatewayStatusClient> logger)
{
    /// <summary>
    /// true=Teams 認證正常、false=失效（cookie 死了）、null=查不到（gateway 不可達）。
    /// null 刻意跟 false 分開：gateway 連不上是另一種問題，不該當成「認證失效」去告警。
    /// </summary>
    public async Task<bool?> IsTeamsAuthOkAsync(CancellationToken ct)
    {
        try
        {
            var status = await http.GetFromJsonAsync("/v1/status", ContractsJsonContext.Default.AuthStatusDto, ct);
            return status is null ? null : status.TeamsAuth == "ok";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "查 /v1/status 失敗，當未知");
            return null;
        }
    }
}
