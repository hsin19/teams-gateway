using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

public class AuthMonitorTests
{
    // 一個頻道發告警失敗（Slack 限流、連不上）不能丟出去：BackgroundService 的例外會讓整個 relay 停掉，
    // 其他頻道也要照發
    [Fact]
    public async Task Alert_failure_on_one_channel_does_not_throw_or_skip_the_rest()
    {
        var slack = new FailFirstSlack();
        var monitor = new AuthMonitor(
            new GatewayStatusClient(new HttpClient(), NullLogger<GatewayStatusClient>.Instance),
            new SlackClient(new HttpClient(slack) { BaseAddress = new Uri("https://slack.com/api/") }, NullLogger<SlackClient>.Instance),
            Options.Create(new RelayOptions()),
            NullLogger<AuthMonitor>.Instance);

        await monitor.AlertAsync(["C1", "C2"], TestContext.Current.CancellationToken);

        Assert.Equal(2, slack.Calls);
    }

    /// <summary>第一個請求丟網路錯誤，之後回成功。</summary>
    private sealed class FailFirstSlack : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++Calls == 1)
                throw new HttpRequestException("Slack unreachable");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"ts":"111.222"}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
