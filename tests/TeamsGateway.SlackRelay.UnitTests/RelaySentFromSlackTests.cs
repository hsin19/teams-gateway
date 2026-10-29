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

    // 重啟後先收到編輯事件：恢復對應後仍要送出 Teams 編輯通知
    [Fact]
    public async Task Recovered_top_level_send_edit_is_posted_to_its_thread()
    {
        var (worker, slack, links, _) = Worker();
        slack.Responses["auth.test"] = """{"ok":true,"user_id":"UBOT"}""";
        slack.Responses["reactions.get"] = """
            {"ok":true,"type":"message","message":{"ts":"1729834567.123456","user":"UME","reactions":[{"name":"outbox_tray","users":["UBOT"],"count":1}]}}
            """;

        await worker.RelayWithRetryAsync(
            Echo() with { Content = "<p>fixed</p>", Version = 1793220599999, Edited = true }, Routes, null, Ct);

        var note = Assert.Single(slack.Requests, r => r.Method == "chat.postMessage").Body;
        Assert.Equal(Ts, note.GetProperty("thread_ts").GetString());
        Assert.StartsWith("_Edited in Teams_", note.GetProperty("text").GetString());
        Assert.Equal(1793220599999, links.Lookup(TeamsId)?.Version);
    }

    // 重啟後先收到刪除事件：補回對應後一樣在那則的 thread 貼 Deleted in Teams（沒有內容也要處理，不能被空內容的檢查擋掉）
    [Fact]
    public async Task Recovered_top_level_send_deletion_is_posted_to_its_thread()
    {
        var (worker, slack, _, _) = Worker();
        slack.Responses["auth.test"] = """{"ok":true,"user_id":"UBOT"}""";
        slack.Responses["reactions.get"] = """
            {"ok":true,"type":"message","message":{"ts":"1729834567.123456","user":"UME","reactions":[{"name":"outbox_tray","users":["UBOT"],"count":1}]}}
            """;

        await worker.RelayWithRetryAsync(
            Echo() with { Content = null, Version = 1793220599999, Deleted = true }, Routes, null, Ct);

        var note = Assert.Single(slack.Requests, r => r.Method == "chat.postMessage").Body;
        Assert.Equal(Ts, note.GetProperty("thread_ts").GetString());
        Assert.StartsWith("_Deleted in Teams_", note.GetProperty("text").GetString());
    }

    // Slack 沒有貼成編輯通知時版本不能前進，否則相同事件重播會被當成已處理
    [Fact]
    public async Task Failed_edit_note_does_not_advance_the_link_version()
    {
        var (worker, slack, links, _) = Worker();
        links.Remember(TeamsId, "C1", Ts, "", null, chat: Chat, byBot: false);
        slack.RespondToBody = (method, _) =>
            method == "chat.postMessage" ? """{"ok":false,"error":"channel_not_found"}""" : null;
        var edited = Echo() with { Content = "<p>fixed</p>", Version = 1793220599999, Edited = true };

        await worker.RelayWithRetryAsync(edited, Routes, null, Ct);

        Assert.Null(links.Lookup(TeamsId)?.Version);
        slack.RespondToBody = null;
        await worker.RelayWithRetryAsync(edited, Routes, null, Ct);

        Assert.Equal(2, slack.Requests.Count(r => r.Method == "chat.postMessage"));
        Assert.Equal(1793220599999, links.Lookup(TeamsId)?.Version);
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
        links.Remember(TeamsId, "C1", Ts, "", null, chat: Chat, byBot: false);
        var edited = Echo() with { Content = "<p>fixed</p>", Version = 1793220599999, Edited = !deleted, Deleted = deleted };

        await worker.RelayWithRetryAsync(edited, Routes, null, Ct);

        Assert.DoesNotContain(slack.Requests, r => r.Method == "chat.update");
        var note = Assert.Single(slack.Requests, r => r.Method == "chat.postMessage").Body;
        Assert.Equal(Ts, note.GetProperty("thread_ts").GetString());
        Assert.StartsWith(label, note.GetProperty("text").GetString());
        Assert.Equal(1793220599999, links.Lookup(TeamsId)?.Version);
    }

    // 重啟後 gateway 重送之前的編輯：thread 裡已經貼過這一版（看留言的 metadata），不要每次重啟都再貼一次
    [Fact]
    public async Task Edit_already_noted_is_not_posted_again_after_a_restart()
    {
        var (worker, slack, links, _) = Worker();
        slack.Responses["auth.test"] = """{"ok":true,"user_id":"UBOT"}""";
        slack.Responses["reactions.get"] = """
            {"ok":true,"type":"message","message":{"ts":"1729834567.123456","user":"UME","reactions":[{"name":"outbox_tray","users":["UBOT"],"count":1}]}}
            """;
        slack.Responses["conversations.replies"] = """
            {"ok":true,"messages":[{"ts":"1729834567.123456","user":"UME","text":"hello"},
              {"ts":"1729834600.000100","bot_id":"B1","text":"_Edited in Teams_","metadata":{"event_type":"teams_edit_note","event_payload":{"teams_id":"1793220563903","version":1793220599999}}}]}
            """;

        await worker.RelayWithRetryAsync(Echo() with { Content = "<p>fixed</p>", Version = 1793220599999, Edited = true }, Routes, null, Ct);

        Assert.DoesNotContain(slack.Requests, r => r.Method == "chat.postMessage");
        Assert.Equal(1793220599999, links.Lookup(TeamsId)?.Version);
    }

    // thread 裡貼過的是舊一版、或是別則的留言：這次的編輯沒貼過，照貼
    [Theory]
    [InlineData("1793220563903", 1793220580000)]
    [InlineData("1793220000000", 1793220599999)]
    public async Task Newer_or_other_edit_is_still_posted_after_a_restart(string notedId, long notedVersion)
    {
        var (worker, slack, links, _) = Worker();
        links.Remember(TeamsId, "C1", Ts, "", null, chat: Chat, byBot: false);
        slack.Responses["conversations.replies"] = $$$$"""
            {"ok":true,"messages":[{"ts":"1729834600.000100","bot_id":"B1","text":"_Edited in Teams_",
              "metadata":{"event_type":"teams_edit_note","event_payload":{"teams_id":"{{{{notedId}}}}","version":{{{{notedVersion}}}}}}}]}
            """;

        await worker.RelayWithRetryAsync(Echo() with { Content = "<p>fixed</p>", Version = 1793220599999, Edited = true }, Routes, null, Ct);

        Assert.Single(slack.Requests, r => r.Method == "chat.postMessage");
        Assert.Equal(1793220599999, links.Lookup(TeamsId)?.Version);
    }

    // 頂層發的那則：API 回應時記了對應、換表情卻失敗；推回來時對應已經在，還是要再試一次換表情，不然一直停在 ⏳
    [Fact]
    public async Task Echo_retries_a_failed_status_swap_for_a_top_level_send()
    {
        var (worker, slack, links, sends) = Worker();
        var send = sends.Register(ClientMessageId, "C1", Ts, inThread: false);
        slack.RespondToBody = (method, _) => method == "reactions.add" ? """{"ok":false,"error":"internal_error"}""" : null;
        await Assert.ThrowsAsync<SlackTransientException>(() => sends.ConfirmAsync(send, TeamsId, Chat, Ct));
        Assert.NotNull(links.Lookup(TeamsId));
        slack.RespondToBody = null;

        await worker.RelayWithRetryAsync(Echo(), Routes, null, Ct);

        Assert.True(send.Confirmed);
        Assert.DoesNotContain(slack.Requests, r => r.Method == "chat.postMessage");
    }

    // 推回來補換表情又失敗（不是限流）：記 warning 就好，不走整筆重試卡住後面的事件
    [Fact]
    public async Task Failed_status_retry_on_echo_does_not_stall_the_stream()
    {
        var (worker, slack, links, sends) = Worker();
        var send = sends.Register(ClientMessageId, "C1", Ts, inThread: false);
        links.Remember(TeamsId, "C1", Ts, "", null, chat: Chat, byBot: false);
        slack.RespondToBody = (method, _) => method == "reactions.add" ? """{"ok":false,"error":"message_not_found"}""" : null;

        await worker.RelayWithRetryAsync(Echo(), Routes, null, Ct).WaitAsync(TimeSpan.FromSeconds(1), Ct);

        Assert.Single(slack.Requests, r => r.Method == "reactions.add");
        Assert.False(send.Confirmed);
    }

    // thread 裡發的推回來時被限流：整筆等一下重來（還沒貼），不能吞掉
    [Fact]
    public async Task Rate_limited_status_swap_retries_the_whole_thread_echo()
    {
        var (worker, slack, _, sends) = Worker(rateLimitFallback: TimeSpan.Zero);
        var send = sends.Register(ClientMessageId, "C1", Ts, inThread: true);
        var limited = true;
        slack.RespondToBody = (method, _) =>
        {
            if (method != "reactions.add" || !limited)
                return null;
            limited = false;
            return """{"ok":false,"error":"ratelimited"}""";
        };

        await worker.RelayWithRetryAsync(Echo(), Routes, null, Ct);

        Assert.True(send.Confirmed);
        Assert.Single(slack.Requests, r => r.Method == "chat.postMessage");
    }

    // 貼出去的「Edited in Teams」夾著 Teams id 與版本，重啟後才認得出貼過了
    [Fact]
    public async Task Edit_note_carries_the_version_it_shows()
    {
        var (worker, slack, links, _) = Worker();
        links.Remember(TeamsId, "C1", Ts, "", null, chat: Chat, byBot: false);

        await worker.RelayWithRetryAsync(Echo() with { Content = "<p>fixed</p>", Version = 1793220599999, Edited = true }, Routes, null, Ct);

        var payload = Assert.Single(slack.Requests, r => r.Method == "chat.postMessage").Body.GetProperty("metadata");
        Assert.Equal("teams_edit_note", payload.GetProperty("event_type").GetString());
        Assert.Equal((TeamsId, 1793220599999L),
            (payload.GetProperty("event_payload").GetProperty("teams_id").GetString(), payload.GetProperty("event_payload").GetProperty("version").GetInt64()));
    }

    // thread 裡發的推回來，換表情失敗（例如那則剛被刪掉）：表情只是顯示，Teams 上已經有這句，頂層照樣要貼
    [Fact]
    public async Task Thread_send_is_posted_even_when_its_status_emoji_fails()
    {
        var (worker, slack, _, sends) = Worker();
        sends.Register(ClientMessageId, "C1", Ts, inThread: true);
        slack.RespondToBody = (method, _) => method == "reactions.add" ? """{"ok":false,"error":"message_not_found"}""" : null;

        await worker.RelayWithRetryAsync(Echo(), Routes, null, Ct);

        Assert.Single(slack.Requests, r => r.Method == "chat.postMessage");
    }

    private static MessageDto Echo() =>
        new(TeamsId, Chat, "8:me", "Me", "RichText/Html", "<p>hello</p>", 1, DateTimeOffset.FromUnixTimeSeconds(1729834567 + 30), long.Parse(TeamsId),
            ClientMessageId: ClientMessageId);

    private static (RelayWorker Worker, RecordingSlack Slack, MessageLinks Links, SendTracker Sends) Worker(TimeSpan? rateLimitFallback = null)
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
            NullLogger<RelayWorker>.Instance) { RateLimitFallback = rateLimitFallback ?? TimeSpan.FromSeconds(30) };
        return (worker, slack, links, sends);
    }
}
