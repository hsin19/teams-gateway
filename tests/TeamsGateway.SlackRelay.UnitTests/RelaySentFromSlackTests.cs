using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>
/// 從 Slack 發過去的話被 Teams 推回來時（ADR 0012）：頂層發的那則就代表 Teams 上的這句，不再貼；thread 裡發的照一般訊息貼到頂層。
/// 推回可能比 API 回應早到、relay 也可能重啟過，兩種都要認得。
/// </summary>
public class RelaySentFromSlackTests
{
    private const string Chat = "19:a@thread.v2";
    private const string Ts = "1729834567.123456";
    private const string ClientMessageId = "1729834567123456";
    private const string TeamsId = "1793220563903";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Dictionary<string, string> Routes => new() { [Chat] = "C1" };

    [Fact]
    public async Task Top_level_send_is_not_posted_again()
    {
        var (worker, slack, links, sends) = Worker();
        sends.Register(ClientMessageId, "C1", Ts, inThread: false);

        await worker.RelayWithRetryAsync(Echo(), Routes, null, Ct);

        Assert.DoesNotContain(slack.Requests, r => r.Method == "chat.postMessage");
        Assert.Contains(slack.Requests, r => r.Method == "reactions.add" && r.Body.GetProperty("name").GetString() == "outbox_tray");
        Assert.Equal(("C1", Ts, false), (links.Lookup(TeamsId)?.Channel, links.Lookup(TeamsId)?.Ts, links.Lookup(TeamsId)?.ByBot));
    }

    // Teams 沒有 thread：thread 裡發的要由 bot 貼到頂層，頂層才拼得出 Teams 上的對話
    [Fact]
    public async Task Thread_send_is_posted_to_the_top_level()
    {
        var (worker, slack, _, sends) = Worker();
        sends.Register(ClientMessageId, "C1", Ts, inThread: true);

        await worker.RelayWithRetryAsync(Echo(), Routes, null, Ct);

        var post = Assert.Single(slack.Requests, r => r.Method == "chat.postMessage");
        Assert.False(post.Body.TryGetProperty("thread_ts", out _));
    }

    // relay 在送出途中重啟：記憶體裡沒有，那則上還停在 ⏳，照樣認得、不再貼
    [Fact]
    public async Task Send_is_recognised_after_a_restart()
    {
        var (worker, slack, links, _) = Worker();
        slack.Responses["auth.test"] = """{"ok":true,"user_id":"UBOT"}""";
        slack.Responses["reactions.get"] = """
            {"ok":true,"type":"message","message":{"ts":"1729834567.123456","user":"UME","reactions":[{"name":"hourglass_flowing_sand","users":["UBOT"],"count":1}]}}
            """;

        await worker.RelayWithRetryAsync(Echo(), Routes, null, Ct);

        Assert.DoesNotContain(slack.Requests, r => r.Method == "chat.postMessage");
        Assert.False(links.Lookup(TeamsId)?.ByBot);
    }

    // 不是從 Slack 發的（例如 MCP 發的，id 是 19 碼）：照 Phase 1 由 bot 轉送，不問 Slack
    [Fact]
    public async Task Other_sends_are_relayed_as_usual()
    {
        var (worker, slack, _, _) = Worker();

        await worker.RelayWithRetryAsync(Echo() with { ClientMessageId = "1234567890123456789" }, Routes, null, Ct);

        Assert.Single(slack.Requests, r => r.Method == "chat.postMessage");
        Assert.DoesNotContain(slack.Requests, r => r.Method == "reactions.get");
    }

    // 頂層發的那則在 Teams 上被改了：bot 改不了人發的訊息，新版貼在它的 thread
    [Theory]
    [InlineData(false, "_Edited in Teams_")]
    [InlineData(true, "_Deleted in Teams_")]
    public async Task Teams_edit_of_a_top_level_send_goes_to_its_thread(bool deleted, string label)
    {
        var (worker, slack, links, _) = Worker();
        links.Remember(TeamsId, "C1", Ts, "", long.Parse(TeamsId), chat: Chat, byBot: false);
        var edited = Echo() with { Content = "<p>fixed</p>", Version = 1793220599999, Edited = !deleted, Deleted = deleted };

        await worker.RelayWithRetryAsync(edited, Routes, null, Ct);

        Assert.DoesNotContain(slack.Requests, r => r.Method == "chat.update");
        var note = Assert.Single(slack.Requests, r => r.Method == "chat.postMessage").Body;
        Assert.Equal(Ts, note.GetProperty("thread_ts").GetString());
        Assert.StartsWith(label, note.GetProperty("text").GetString());
        Assert.Equal(1793220599999, links.Lookup(TeamsId)?.Version);
    }

    private static MessageDto Echo() =>
        new(TeamsId, Chat, "8:me", "Me", "RichText/Html", "<p>hello</p>", 1, DateTimeOffset.FromUnixTimeSeconds(1729834567 + 30), long.Parse(TeamsId),
            ClientMessageId: ClientMessageId);

    private static (RelayWorker Worker, RecordingSlack Slack, MessageLinks Links, SendTracker Sends) Worker()
    {
        var slack = new RecordingSlack();
        var links = new MessageLinks();
        var options = new RelayOptions { Slack = { AppToken = "xapp-1" } };
        var sends = RelayFakes.Sends(slack, links, options);
        var worker = new RelayWorker(
            new SseEventSource(new GatewayEventStream(new HttpClient(), NullLogger<GatewayEventStream>.Instance), new MessageLinks(), NullLogger<SseEventSource>.Instance),
            RelayFakes.Slack(slack),
            new GatewayMediaClient(new HttpClient(), NullLogger<GatewayMediaClient>.Instance),
            links,
            sends,
            Options.Create(options),
            NullLogger<RelayWorker>.Instance);
        return (worker, slack, links, sends);
    }
}
