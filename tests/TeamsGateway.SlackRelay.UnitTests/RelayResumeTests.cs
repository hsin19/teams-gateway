using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    private static Dictionary<string, string> Routes => new() { ["19:x@thread.v2"] = "C1" };

    private static (RelayWorker Worker, MessageLinks Links, RecordingSlack Slack) Worker()
    {
        var slack = new RecordingSlack();
        var links = new MessageLinks();
        var worker = new RelayWorker(
            new GatewayEventStream(new HttpClient(), NullLogger<GatewayEventStream>.Instance),
            new SlackClient(new HttpClient(slack) { BaseAddress = new Uri("https://slack.com/api/") }, NullLogger<SlackClient>.Instance),
            new GatewayMediaClient(new HttpClient(), NullLogger<GatewayMediaClient>.Instance),
            links,
            Options.Create(new RelayOptions()),
            NullLogger<RelayWorker>.Instance);
        return (worker, links, slack);
    }
}
