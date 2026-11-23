namespace TeamsGateway.SlackRelay;

/// <summary>
/// gateway 綁的 Teams 帳號是誰（<c>/v1/status</c> 的 <c>accountMri</c>），查到就記住。
/// 經這個帳號發的訊息（從 Slack、別的 API key、MCP 發的）推回來轉貼時，metadata 標 <c>from_self</c>：
/// 讀頻道的 agent 靠它認出是自己這邊送出去的話，不當成對方新說的。
/// 查不到（gateway 還沒換到 token、連不上）就先當不是，下一則再查：標漏只是少一個提示，不影響轉貼。
/// </summary>
public sealed class TeamsAccount(GatewayStatusClient gateway)
{
    private string? _mri;

    public async Task<bool> IsSelfAsync(string? from, CancellationToken ct) =>
        !string.IsNullOrEmpty(from) && from == (_mri ??= await gateway.GetAccountMriAsync(ct));
}
