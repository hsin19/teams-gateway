using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>回覆一則轉過的訊息：第一行「*Bob* Reply Alice」（連回被引用那則、不加冒號），內容在下一行。內容是合成的假資料。</summary>
public class RelayReplyTests
{
    private const string Conv = "19:x@thread.v2";

    [Fact]
    public async Task Reply_header_reads_name_then_reply_link()
    {
        var slack = new RecordingSlack();
        var links = new MessageLinks();
        links.Remember("1792640000000", "C1", "111.222", "*Alice*:\nhello", 1792640000000);
        var worker = new RelayWorker(
            new GatewayEventStream(new HttpClient(), NullLogger<GatewayEventStream>.Instance),
            new SlackClient(new HttpClient(slack) { BaseAddress = new Uri("https://slack.com/api/") }, NullLogger<SlackClient>.Instance),
            new GatewayMediaClient(new HttpClient(), NullLogger<GatewayMediaClient>.Instance),
            links,
            Options.Create(new RelayOptions()),
            NullLogger<RelayWorker>.Instance);
        const string content =
            "<blockquote itemscope=\"\" itemtype=\"http://schema.skype.com/Reply\" itemid=\"1792640000000\">" +
            "<strong itemprop=\"mri\" itemid=\"8:alice\">Alice</strong><span itemprop=\"time\" itemid=\"1792640000000\"></span>" +
            "<p itemprop=\"preview\">hello</p></blockquote><p>thanks</p>";
        var reply = new MessageDto("1792640099000", Conv, "8:bob", "Bob", "RichText/Html", content, 2, DateTimeOffset.UtcNow, 1792640099000);

        await worker.RelayWithRetryAsync(reply, new Dictionary<string, string> { [Conv] = "C1" }, null, TestContext.Current.CancellationToken);

        var post = slack.Requests.Single(r => r.Method == "chat.postMessage");
        Assert.Equal($"*Bob* <{RecordingSlack.Permalink}|Reply Alice>\nthanks", post.Body.GetProperty("text").GetString());
    }
}
