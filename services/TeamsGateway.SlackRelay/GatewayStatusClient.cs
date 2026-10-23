using System.Net.Http.Json;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay;

/// <summary>查 gateway 的 <c>/v1/status</c>（Teams 認證健康）與 <c>/v1/chats</c>（對話名稱，啟動時印路由用）。</summary>
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

    /// <summary>帳號的 Teams 對話 id → 名稱（1:1 沒有名稱，是 null）；查不到回 null，只拿來印 log。</summary>
    public async Task<IReadOnlyDictionary<string, string?>?> GetChatTopicsAsync(CancellationToken ct)
    {
        try
        {
            var chats = await http.GetFromJsonAsync("/v1/chats", ContractsJsonContext.Default.IReadOnlyListConversationDto, ct) ?? [];
            return chats.DistinctBy(c => c.Id).ToDictionary(c => c.Id, c => c.Topic);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "查 gateway /v1/chats 失敗，路由只印 id");
            return null;
        }
    }
}
