using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>
/// 回覆轉過的訊息：第一行「*Bob* replied to Alice」（名字連回被引用那則、不加冒號），內容在下一行。
/// Teams 可以一次引用好幾則，每一則都要接得上。內容是合成的假資料。
/// </summary>
public class RelayReplyTests
{
    private const string Conv = "19:x@thread.v2";

    private static string QuoteHtml(string id, string author, string preview, string? mri = null) =>
        $"<blockquote itemscope=\"\" itemtype=\"http://schema.skype.com/Reply\" itemid=\"{id}\">" +
        $"<strong itemprop=\"mri\" itemid=\"{mri ?? $"8:{author.ToLowerInvariant()}"}\">{author}</strong><span itemprop=\"time\" itemid=\"{id}\"></span>" +
        $"<p itemprop=\"preview\">{preview}</p></blockquote>";

    [Fact]
    public async Task Single_reply_header_reads_name_then_reply_link()
    {
        var text = await RelayAsync(QuoteHtml("100", "Alice", "hello") + "<p>thanks</p>", ("100", "111.100"));

        Assert.Equal($"*Bob* replied to <{RecordingSlack.Permalink("111.100")}|Alice>\nthanks", text);
    }

    [Fact]
    public async Task Every_quoted_message_gets_a_link()
    {
        var text = await RelayAsync(
            QuoteHtml("100", "Alice", "123") + "<p></p>" + QuoteHtml("200", "Carol", "456") + "<p>789</p>",
            ("100", "111.100"), ("200", "111.200"));

        Assert.Equal($"*Bob* replied to <{RecordingSlack.Permalink("111.100")}|Alice>, <{RecordingSlack.Permalink("111.200")}|Carol>\n789", text);
    }

    // 引用的其中一則沒轉過：有轉過的接連結，沒轉過的照樣畫成 blockquote
    [Fact]
    public async Task Unrelayed_quote_stays_a_blockquote()
    {
        var text = await RelayAsync(
            QuoteHtml("100", "Alice", "123") + "<p></p>" + QuoteHtml("200", "Carol", "456") + "<p>789</p>",
            ("100", "111.100"));

        Assert.Equal($"*Bob* replied to <{RecordingSlack.Permalink("111.100")}|Alice>\n> *Carol*\n> 456\n789", text);
    }

    // 引用 HTML 裡的作者名是 Teams 塞的（實際看過 "Display Name"）：照 MRI 查回本名，像 Teams 自己那樣
    [Fact]
    public async Task Quoting_yourself_shows_your_name_not_the_html_one()
    {
        var text = await RelayAsync(QuoteHtml("100", "Display Name", "123", mri: "8:bob") + "<p>789</p>", ("100", "111.100"));

        Assert.Equal($"*Bob* replied to <{RecordingSlack.Permalink("111.100")}|Bob>\n789", text);
    }

    [Fact]
    public async Task Quoted_author_seen_earlier_is_resolved_by_mri()
    {
        var text = await RelayAsync(
            QuoteHtml("100", "Display Name", "123", mri: "8:alice") + QuoteHtml("200", "Display Name", "456", mri: "8:alice") + "<p>789</p>",
            relayed: [("100", "111.100")],
            earlierSenders: [("8:alice", "Alice Chen")]);

        // 有連結的、沒轉過畫成 blockquote 的，都用查到的名字
        Assert.Equal($"*Bob* replied to <{RecordingSlack.Permalink("111.100")}|Alice Chen>\n> *Alice Chen*\n> 456\n789", text);
    }

    private static Task<string?> RelayAsync(string content, params (string TeamsId, string Ts)[] relayed) =>
        RelayAsync(content, relayed, earlierSenders: []);

    /// <summary>
    /// 轉一則 Bob 的回覆，回傳貼到 Slack 的文字。<paramref name="relayed"/> 是已經轉過的（Teams id, Slack ts）；
    /// <paramref name="earlierSenders"/> 是之前講過話的人（先各轉一則沒路由的訊息，讓 relay 記住名字）。
    /// </summary>
    private static async Task<string?> RelayAsync(
        string content, (string TeamsId, string Ts)[] relayed, (string Mri, string Name)[] earlierSenders)
    {
        var slack = new RecordingSlack();
        var links = new MessageLinks();
        foreach (var (teamsId, ts) in relayed)
            links.Remember(teamsId, "C1", ts, "*Someone*:\nquoted", long.Parse(teamsId));
        var worker = new RelayWorker(
            new SseEventSource(new GatewayEventStream(new HttpClient(), NullLogger<GatewayEventStream>.Instance), new MessageLinks(), NullLogger<SseEventSource>.Instance),
            new SlackClient(new HttpClient(slack) { BaseAddress = new Uri("https://slack.com/api/") }, NullLogger<SlackClient>.Instance),
            new GatewayMediaClient(new HttpClient(), NullLogger<GatewayMediaClient>.Instance),
            links,
            RelayFakes.Sends(slack, links),
            RelayFakes.Bot(slack),
            Options.Create(new RelayOptions()),
            NullLogger<RelayWorker>.Instance);
        var routes = new Dictionary<string, string> { [Conv] = "C1" };
        var ct = TestContext.Current.CancellationToken;
        foreach (var (mri, name) in earlierSenders)
            await worker.RelayWithRetryAsync(
                new MessageDto("1792630000000", "19:unrouted@thread.v2", mri, name, "Text", "hi", 1, DateTimeOffset.UtcNow, 1792630000000), routes, null, ct);
        var reply = new MessageDto("1792640099000", Conv, "8:bob", "Bob", "RichText/Html", content, 2, DateTimeOffset.UtcNow, 1792640099000);

        await worker.RelayWithRetryAsync(reply, routes, null, ct);

        return slack.Requests.Single(r => r.Method == "chat.postMessage").Body.GetProperty("text").GetString();
    }
}
