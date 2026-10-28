using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>
/// relay 重啟後的續接點 = Slack 上最新一則轉過去的 Teams 訊息。只看近期歷史的話，
/// 近期沒轉過東西時續接點會退回「現在」，停機期間 gateway 發出的事件就漏了。
/// </summary>
public class RelayResumeTests
{
    private const string Relayed = """
        {"ok":true,"messages":[{"ts":"1792000000.000100","text":"*Bob*:\nhi","metadata":{"event_type":"teams_relay","event_payload":{"teams_id":"T1","version":1}}}]}
        """;
    private const string Empty = """{"ok":true,"messages":[]}""";

    // 近 24 小時沒轉過東西（安靜的對話、週末、Slack 剛清過）：往更早的歷史找最新一則當續接點
    [Fact]
    public async Task Resume_point_comes_from_older_history_when_nothing_was_relayed_recently()
    {
        var (worker, links, slack) = Worker();
        slack.Respond = uri => !uri.AbsolutePath.EndsWith("conversations.history", StringComparison.Ordinal) ? null
            : uri.Query.Contains("oldest=", StringComparison.Ordinal) ? Empty : Relayed;

        await worker.WarmupLinksAsync(Routes, null, 24, TestContext.Current.CancellationToken);

        Assert.Equal("T1", links.NewestTeamsId());
    }

    // 近期就找得到：不用再往前翻
    [Fact]
    public async Task Recent_history_needs_no_older_lookup()
    {
        var (worker, links, slack) = Worker();
        slack.Responses["conversations.history"] = Relayed;

        await worker.WarmupLinksAsync(Routes, null, 24, TestContext.Current.CancellationToken);

        Assert.Equal("T1", links.NewestTeamsId());
        Assert.Single(slack.Requests, r => r.Method == "conversations.history");
    }

    // 頻道裡從來沒轉過（第一次啟動）：沒有續接點，從現在開始
    [Fact]
    public async Task No_relayed_message_at_all_leaves_no_resume_point()
    {
        var (worker, links, slack) = Worker();
        slack.Responses["conversations.history"] = Empty;

        await worker.WarmupLinksAsync(Routes, null, 24, TestContext.Current.CancellationToken);

        Assert.Null(links.NewestTeamsId());
    }

    // 從 Slack 回覆時要知道送去哪個對話：一個頻道可能對到多個對話，所以對話 id 跟 Teams id 一起夾在 metadata 裡
    [Fact]
    public async Task Relayed_message_metadata_carries_the_chat()
    {
        var (worker, _, slack) = Worker();
        var message = new MessageDto("1792640000000", "19:x@thread.v2", "8:bob", "Bob", "Text", "hi", 1, DateTimeOffset.UtcNow, 1792640000000);

        await worker.RelayWithRetryAsync(message, Routes, null, TestContext.Current.CancellationToken);

        var post = Assert.Single(slack.Requests, r => r.Method == "chat.postMessage");
        Assert.Equal("19:x@thread.v2", post.Body.GetProperty("metadata").GetProperty("event_payload").GetProperty("chat").GetString());
    }

    // 重啟回補時連對話一起記回來；這個欄位上線前轉的訊息沒有，是 null
    [Fact]
    public async Task Warmup_remembers_the_chat_from_metadata()
    {
        var (worker, links, slack) = Worker();
        slack.Responses["conversations.history"] = """
            {"ok":true,"messages":[
              {"ts":"1792000000.000200","text":"*Bob*:\nnew","metadata":{"event_type":"teams_relay","event_payload":{"teams_id":"T2","version":2,"chat":"19:x@thread.v2"}}},
              {"ts":"1792000000.000100","text":"*Bob*:\nold","metadata":{"event_type":"teams_relay","event_payload":{"teams_id":"T1","version":1}}}]}
            """;

        await worker.WarmupLinksAsync(Routes, null, 24, TestContext.Current.CancellationToken);

        Assert.Equal("19:x@thread.v2", links.Lookup("T2")?.Chat);
        Assert.Null(links.Lookup("T1")?.Chat);
    }

    private static Dictionary<string, string> Routes => new() { ["19:x@thread.v2"] = "C1" };

    private static (RelayWorker Worker, MessageLinks Links, RecordingSlack Slack) Worker()
    {
        var slack = new RecordingSlack();
        var links = new MessageLinks();
        var worker = new RelayWorker(
            new SseEventSource(new GatewayEventStream(new HttpClient(), NullLogger<GatewayEventStream>.Instance), new MessageLinks(), NullLogger<SseEventSource>.Instance),
            new SlackClient(new HttpClient(slack) { BaseAddress = new Uri("https://slack.com/api/") }, NullLogger<SlackClient>.Instance),
            new GatewayMediaClient(new HttpClient(), NullLogger<GatewayMediaClient>.Instance),
            links,
            Options.Create(new RelayOptions()),
            NullLogger<RelayWorker>.Instance);
        return (worker, links, slack);
    }
}
