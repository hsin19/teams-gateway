using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>單則轉送失敗：重試，還是不行就放棄這則，不能把例外丟給外層（會被當成 SSE 斷線、訊息默默消失）。</summary>
public class RelayRetryTests
{
    private const string Conv = "19:x@thread.v2";
    private static readonly Dictionary<string, string> Routes = new() { [Conv] = "C1" };
    private static readonly MessageDto Message = new("1792640000000", Conv, "8:me", "Me", "Text", "hi", 1, DateTimeOffset.UtcNow, 1792640000000);

    [Fact]
    public async Task Transient_slack_failure_is_retried()
    {
        var slack = new FlakySlack(failures: 1);
        var (worker, links) = Worker(slack);

        await worker.RelayWithRetryAsync(Message, Routes, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, slack.Calls);
        Assert.Equal("111.222", links.Lookup(Message.Id)?.Ts);
    }

    [Fact]
    public async Task Persistent_failure_gives_up_without_throwing()
    {
        var slack = new FlakySlack(failures: int.MaxValue);
        var (worker, links) = Worker(slack);

        await worker.RelayWithRetryAsync(Message, Routes, null, TestContext.Current.CancellationToken);

        Assert.Equal(3, slack.Calls); // 第一次 + 兩次重試
        Assert.Null(links.Lookup(Message.Id));
    }

    private static (RelayWorker Worker, MessageLinks Links) Worker(HttpMessageHandler slackHandler)
    {
        var links = new MessageLinks();
        var unused = new HttpClient(new FlakySlack(int.MaxValue)) { BaseAddress = new Uri("http://localhost") };
        var worker = new RelayWorker(
            new GatewayEventStream(unused, NullLogger<GatewayEventStream>.Instance),
            new SlackClient(new HttpClient(slackHandler) { BaseAddress = new Uri("https://slack.com/api/") }, NullLogger<SlackClient>.Instance),
            new GatewayMediaClient(unused, NullLogger<GatewayMediaClient>.Instance),
            links,
            Options.Create(new RelayOptions()),
            NullLogger<RelayWorker>.Instance)
        {
            RetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
        };
        return (worker, links);
    }

    /// <summary>前 <paramref name="failures"/> 次丟網路錯誤，之後回 Slack 成功。</summary>
    private sealed class FlakySlack(int failures) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++Calls <= failures)
                throw new HttpRequestException("Slack 暫時連不上");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"ts":"111.222"}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
