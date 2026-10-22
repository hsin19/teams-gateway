using Microsoft.Extensions.Options;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// 輪詢 gateway 的 Teams 認證健康，在「本來正常、翻成失效」時發一次同步中斷告警到所有頻道。
/// 只發這一次（不洗頻道、恢復也不發）；連續失敗數到門檻才算，避開網路瞬斷誤報。
/// </summary>
public sealed class AuthMonitor(
    GatewayStatusClient status,
    SlackClient slack,
    IOptions<RelayOptions> options,
    ILogger<AuthMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private const int FailuresBeforeAlert = 2; // 連續 N 次 failed 才告警

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var channels = AlertChannels(options.Value.Slack);
        if (channels.Count == 0)
            return; // 沒頻道可告警就不監控

        var gate = new AuthAlertGate(FailuresBeforeAlert);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }

            var ok = await status.IsTeamsAuthOkAsync(ct);
            if (gate.Observe(ok))
                await AlertAsync(channels, ct);
        }
    }

    private async Task AlertAsync(IReadOnlyList<string> channels, CancellationToken ct)
    {
        const string text =
            ":warning: *Teams 同步中斷*\n" +
            "gateway 與 Teams 的連線認證失效（多半是登入 cookie 到期或帳號被登出），" +
            "這個頻道暫時不會再收到新的 Teams 訊息。需重新匯入 cookie 後才會恢復。";

        logger.LogWarning("Teams 認證失效，發同步中斷告警到 {Count} 個頻道", channels.Count);
        foreach (var channel in channels)
            await slack.PostMessageAsync(channel, text, teamsId: null, version: null, images: [], ct);
    }

    /// <summary>所有要告警的頻道 = Routes 的頻道 + DefaultChannel，去重。</summary>
    private static List<string> AlertChannels(RelayOptions.SlackSettings slack)
    {
        var channels = new HashSet<string>(slack.Routes.Select(r => r.Channel));
        if (!string.IsNullOrWhiteSpace(slack.DefaultChannel))
            channels.Add(slack.DefaultChannel);
        channels.RemoveWhere(string.IsNullOrWhiteSpace);
        return [.. channels];
    }
}

/// <summary>
/// 告警閘門：吃一連串「認證是否正常」的觀測，只在「本來正常、連續失效到門檻」時回一次 true。
/// null（未知 / gateway 不可達）不改變狀態；恢復後會重置，下一次中斷會再放行一次。
/// </summary>
public sealed class AuthAlertGate(int failuresBeforeAlert)
{
    private bool _seenOk;
    private bool _alerted;
    private int _failStreak;

    public bool Observe(bool? ok)
    {
        if (ok is null)
            return false; // 未知：狀態不動

        if (ok.Value)
        {
            _seenOk = true;
            _failStreak = 0;
            _alerted = false; // 恢復：之後再斷會再放行一次
            return false;
        }

        if (!_seenOk)
            return false; // 還沒成功過（bootstrap 中），失效不算「中斷」

        if (++_failStreak >= failuresBeforeAlert && !_alerted)
        {
            _alerted = true;
            return true;
        }
        return false;
    }
}
