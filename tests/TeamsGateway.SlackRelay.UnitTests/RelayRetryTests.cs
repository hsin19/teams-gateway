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

    // Slack 限流不是丟例外，是 HTTP 429 + {"ok":false,"error":"ratelimited"}：一樣要重試，不然那則就掉了
    [Fact]
    public async Task Rate_limited_post_is_retried()
    {
        var slack = new ScriptedSlack(RateLimited, Ok);
        var (worker, links) = Worker(slack);

        await worker.RelayWithRetryAsync(Message, Routes, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, slack.Calls);
        Assert.Equal("111.222", links.Lookup(Message.Id)?.Ts);
    }

    // 限流一定會過去：等多久照 Slack 說的，不算重試次數（一般錯誤只重試兩次，限流連來三次也要撐過去）
    [Fact]
    public async Task Rate_limit_does_not_use_up_retries()
    {
        var slack = new ScriptedSlack(RateLimited, RateLimited, RateLimited, Ok);
        var (worker, links) = Worker(slack);

        await worker.RelayWithRetryAsync(Message, Routes, null, TestContext.Current.CancellationToken);

        Assert.Equal(4, slack.Calls);
        Assert.Equal("111.222", links.Lookup(Message.Id)?.Ts);
    }

    // Slack 自己出錯（ok:false + internal_error）是暫時的，跟網路錯誤一樣重試
    [Fact]
    public async Task Slack_internal_error_is_retried()
    {
        var slack = new ScriptedSlack(Error("internal_error"), Ok);
        var (worker, links) = Worker(slack);

        await worker.RelayWithRetryAsync(Message, Routes, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, slack.Calls);
        Assert.Equal("111.222", links.Lookup(Message.Id)?.Ts);
    }

    // 編輯：chat.update 被拒時 Slack 上還是舊版，對照表不能記成新版（不然之後就以為已經更新過了）
    [Fact]
    public async Task Failed_edit_keeps_the_shown_version()
    {
        // 留證的 thread 回覆成功，原地更新被拒
        var slack = new ScriptedSlack(Ok, Error("cant_update_message"));
        var (worker, links) = Worker(slack);
        links.Remember(Message.Id, "C1", "111.222", "*Me*:\nhi", Message.Version);
        var edited = Message with { Content = "hi (edited)", Version = Message.Version + 1000, Edited = true };

        await worker.RelayWithRetryAsync(edited, Routes, null, TestContext.Current.CancellationToken);

        var shown = links.Lookup(Message.Id);
        Assert.Equal(Message.Version, shown?.Version);
        Assert.Equal("*Me*:\nhi", shown?.Text);
    }

    private static readonly (HttpStatusCode Status, string Body) Ok = (HttpStatusCode.OK, """{"ok":true,"ts":"111.222"}""");
    private static readonly (HttpStatusCode Status, string Body) RateLimited = (HttpStatusCode.TooManyRequests, """{"ok":false,"error":"ratelimited"}""");

    private static (HttpStatusCode Status, string Body) Error(string error) => (HttpStatusCode.OK, $$"""{"ok":false,"error":"{{error}}"}""");

    private static (RelayWorker Worker, MessageLinks Links) Worker(HttpMessageHandler slackHandler)
    {
        var links = new MessageLinks();
        var unused = new HttpClient(new FlakySlack(int.MaxValue)) { BaseAddress = new Uri("http://localhost") };
        var worker = new RelayWorker(
            new SseEventSource(new GatewayEventStream(unused, NullLogger<GatewayEventStream>.Instance), links, NullLogger<SseEventSource>.Instance),
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

    /// <summary>照順序回給定的回應（429 時帶 <c>Retry-After: 0</c>，測試不用真的等）；用完就一直回最後一個。</summary>
    private sealed class ScriptedSlack(params (HttpStatusCode Status, string Body)[] script) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = script[Math.Min(Calls++, script.Length - 1)];
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new(TimeSpan.Zero);
            return Task.FromResult(response);
        }
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
