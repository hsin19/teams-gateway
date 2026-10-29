using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>從 Slack 發到 Teams 的那幾則：clientMessageId 與 Slack ts 互換、推回來時認出是自己發的、換狀態表情（ADR 0012）。</summary>
public class SendTrackerTests
{
    private const string Ts = "1729834567.123456";
    private const string ClientMessageId = "1729834567123456";
    private static readonly DateTimeOffset SentAt = DateTimeOffset.FromUnixTimeSeconds(1729834567);

    private const string AuthTest = """{"ok":true,"user_id":"UBOT"}""";

    [Fact]
    public void Client_message_id_is_the_ts_without_the_dot() => Assert.Equal(ClientMessageId, SendTracker.ClientMessageId(Ts));

    // 換回 ts 只用字串處理；Teams 自己的 client 的 id 也有 16 碼，換回的時間要在訊息之前一段時間內才算
    [Theory]
    [InlineData(ClientMessageId, 30, Ts)]                // 按 Send 後 30 秒推回來
    [InlineData(ClientMessageId, 23 * 3600, Ts)]         // 隔了快一天才按 Send
    [InlineData(ClientMessageId, 25 * 3600, null)]       // 太早：當成亂數
    [InlineData(ClientMessageId, -3600, null)]           // 比訊息還晚：當成亂數
    [InlineData("4823749283749283", 30, null)]           // Teams client 的 16 碼亂數（換回是 2122 年）
    [InlineData("1792447868957", 30, null)]              // 13 碼的毫秒時間戳
    [InlineData("1234567890123456789", 30, null)]        // gateway 自己產生的 19 碼
    [InlineData("17298345671234ab", 30, null)]
    public void Ts_comes_back_only_for_plausible_ids(string clientMessageId, int secondsLater, string? expected) =>
        Assert.Equal(expected, SendTracker.TsFromClientMessageId(clientMessageId, SentAt.AddSeconds(secondsLater)));

    // 送出前登記過：推回來直接認得，不用問 Slack
    [Fact]
    public async Task Registered_send_matches_without_asking_slack()
    {
        var (tracker, slack, _) = Tracker();
        tracker.Register(ClientMessageId, "C1", Ts, inThread: true);

        var sent = await tracker.MatchAsync(Echo(), "C1", TestContext.Current.CancellationToken);

        Assert.NotNull(sent);
        Assert.True(sent.InThread);
        Assert.Empty(slack.Requests);
    }

    // relay 重啟過、記憶體裡沒有：看那則 Slack 訊息上有沒有 bot 的狀態表情（重啟時可能還停在 ⏳）
    [Theory]
    [InlineData("hourglass_flowing_sand", "UBOT", true)]
    [InlineData("outbox_tray", "UBOT", true)]
    [InlineData("warning", "UBOT", true)]
    [InlineData("outbox_tray", "UME", false)]   // 人自己按的不算
    [InlineData("eyes", "UBOT", false)]
    public async Task After_a_restart_the_bots_status_reaction_identifies_the_send(string emoji, string by, bool expected)
    {
        var (tracker, slack, _) = Tracker();
        slack.Responses["auth.test"] = AuthTest;
        slack.Responses["reactions.get"] = $$$"""
            {"ok":true,"type":"message","message":{"ts":"{{{Ts}}}","user":"UME","reactions":[{"name":"{{{emoji}}}","users":["{{{by}}}"],"count":1}]}}
            """;

        var sent = await tracker.MatchAsync(Echo(), "C1", TestContext.Current.CancellationToken);

        Assert.Equal(expected, sent is not null);
        if (sent is not null)
            Assert.Equal(("C1", Ts, false), (sent.Channel, sent.Ts, sent.InThread));
    }

    // 沒開發話：推回來的不去問 Slack
    [Fact]
    public async Task Nothing_is_looked_up_when_sending_is_off()
    {
        var (tracker, slack, _) = Tracker(appToken: null);

        Assert.Null(await tracker.MatchAsync(Echo(), "C1", TestContext.Current.CancellationToken));
        Assert.Empty(slack.Requests);
    }

    // 送到了：先按上已送出再拿掉 ⏳ / ⚠️（中間失敗時至少還帶著一個狀態）；頂層發的那則記進對應表（不是 bot 的訊息、版本不記）
    [Fact]
    public async Task Confirm_swaps_reactions_and_links_a_top_level_send()
    {
        var (tracker, slack, links) = Tracker();
        var send = tracker.Register(ClientMessageId, "C1", Ts, inThread: false);

        await tracker.ConfirmAsync(send, "1793220563903", "19:a@thread.v2", TestContext.Current.CancellationToken);
        await tracker.ConfirmAsync(send, "1793220563903", "19:a@thread.v2", TestContext.Current.CancellationToken); // 推回來時又叫一次

        Assert.Equal(
            [("reactions.add", "outbox_tray"), ("reactions.remove", "hourglass_flowing_sand"), ("reactions.remove", "warning")],
            slack.Requests.Select(r => (r.Method, r.Body.GetProperty("name").GetString())));
        var link = links.Lookup("1793220563903");
        Assert.Equal(("C1", Ts, false, "19:a@thread.v2", (long?)null), (link?.Channel, link?.Ts, link?.ByBot, link?.Chat, link?.Version));
    }

    // 自訂的已送出表情 Slack 不認得（打錯、還沒上傳）：退回 📤，之後都用 📤
    [Fact]
    public async Task Unknown_custom_emoji_falls_back_to_the_default()
    {
        var (tracker, slack, _) = Tracker(sentEmoji: "teams-relay");
        slack.RespondToBody = (method, body) =>
            method == "reactions.add" && body.GetProperty("name").GetString() == "teams-relay" ? """{"ok":false,"error":"invalid_name"}""" : null;

        await tracker.ConfirmAsync(tracker.Register(ClientMessageId, "C1", Ts, inThread: true), null, null, TestContext.Current.CancellationToken);
        await tracker.ConfirmAsync(tracker.Register("1729834568000000", "C1", "1729834568.000000", inThread: true), null, null, TestContext.Current.CancellationToken);

        var added = slack.Requests.Where(r => r.Method == "reactions.add").Select(r => r.Body.GetProperty("name").GetString());
        Assert.Equal(["outbox_tray", "teams-relay", "outbox_tray"], added);
    }

    // 不管設了哪個自訂表情都會按上固定的 📤；之後設定改名、重啟，舊訊息照樣認得
    [Fact]
    public async Task Stable_success_marker_survives_a_sent_emoji_change()
    {
        var (original, slack, _) = Tracker(sentEmoji: "old-name");
        await original.ConfirmAsync(original.Register(ClientMessageId, "C1", Ts, inThread: true), null, null, TestContext.Current.CancellationToken);
        var added = slack.Requests.Where(r => r.Method == "reactions.add").Select(r => r.Body.GetProperty("name").GetString()!).ToList();
        Assert.Equal(["outbox_tray", "old-name"], added);

        // 重啟後讀到的是那則上實際按了的表情
        slack.Responses["auth.test"] = AuthTest;
        var reactions = string.Join(",", added.Select(name => $$"""{"name":"{{name}}","users":["UBOT"],"count":1}"""));
        slack.Responses["reactions.get"] = $$$"""
            {"ok":true,"type":"message","message":{"ts":"{{{Ts}}}","user":"UME","reactions":[{{{reactions}}}]}}
            """;
        var changed = RelayFakes.Sends(slack, new MessageLinks(),
            new RelayOptions { Slack = { AppToken = "xapp-1", SentEmoji = "new-name" } });

        Assert.NotNull(await changed.MatchAsync(Echo(), "C1", TestContext.Current.CancellationToken));
    }

    // Slack 的暫時性錯誤不能被當成成功；⏳ 保留，Confirmed 也不能設起來
    [Fact]
    public async Task Failed_success_marker_write_preserves_the_pending_send()
    {
        var (tracker, slack, _) = Tracker();
        slack.RespondToBody = (method, _) =>
            method == "reactions.add" ? """{"ok":false,"error":"internal_error"}""" : null;
        var send = tracker.Register(ClientMessageId, "C1", Ts, inThread: true);

        await Assert.ThrowsAsync<SlackTransientException>(() =>
            tracker.ConfirmAsync(send, null, null, TestContext.Current.CancellationToken));

        Assert.False(send.Confirmed);
        Assert.DoesNotContain(slack.Requests, r => r.Method == "reactions.remove");
    }

    // 停止點是 Slack ts 的秒數減掉 5 分鐘的時鐘誤差；看不懂的 id 沒有停止點
    [Fact]
    public void Earliest_send_is_the_slack_second_minus_the_clock_skew()
    {
        Assert.Equal(SentAt - TimeSpan.FromMinutes(5), SendTracker.EarliestSend(ClientMessageId));
        Assert.Null(SendTracker.EarliestSend("not-a-slack-ts"));
    }

    // 推回來看到了（送到了）但換表情失敗：發話那邊之後的「標失敗」不能把它標成失敗
    [Fact]
    public async Task A_delivered_send_is_not_marked_failed_even_if_its_emoji_failed()
    {
        var (tracker, slack, _) = Tracker();
        var send = tracker.Register(ClientMessageId, "C1", Ts, inThread: true);
        slack.RespondToBody = (method, _) => method == "reactions.add" ? """{"ok":false,"error":"internal_error"}""" : null;
        await Assert.ThrowsAsync<SlackTransientException>(() => tracker.ConfirmAsync(send, null, null, TestContext.Current.CancellationToken));
        slack.RespondToBody = null;

        Assert.False(await tracker.TryMarkFailedAsync(send, ClientMessageId, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(slack.Requests, r => r.Method == "reactions.add" && r.Body.GetProperty("name").GetString() == "warning");
    }

    // 推回那邊已經確認送到：發話那邊之後的「標失敗」什麼都不做（不碰表情、不拿掉登記）
    [Fact]
    public async Task Marking_a_confirmed_send_as_failed_does_nothing()
    {
        var (tracker, slack, _) = Tracker();
        var send = tracker.Register(ClientMessageId, "C1", Ts, inThread: true);
        await tracker.ConfirmAsync(send, null, null, TestContext.Current.CancellationToken);
        var calls = slack.Requests.Count;

        Assert.False(await tracker.TryMarkFailedAsync(send, ClientMessageId, TestContext.Current.CancellationToken));
        Assert.Equal(calls, slack.Requests.Count);
        Assert.Same(send, await tracker.MatchAsync(Echo(), "C1", TestContext.Current.CancellationToken));
    }

    // API 回應沒帶 Teams id 就先確認了：推回來那次還是要把頂層那則記進對應表
    [Fact]
    public async Task Echo_records_the_link_after_a_confirmation_without_an_id()
    {
        var (tracker, _, links) = Tracker();
        var send = tracker.Register(ClientMessageId, "C1", Ts, inThread: false);

        await tracker.ConfirmAsync(send, null, "19:a@thread.v2", TestContext.Current.CancellationToken);
        await tracker.ConfirmAsync(send, "1793220563903", "19:a@thread.v2", TestContext.Current.CancellationToken);

        Assert.Equal(("C1", Ts, false), (links.Lookup("1793220563903")?.Channel, links.Lookup("1793220563903")?.Ts, links.Lookup("1793220563903")?.ByBot));
    }

    // 自訂的顯示表情按失敗（不是不認得，是 Slack 出錯）：📤 已經在，送出照樣算確認
    [Fact]
    public async Task Display_emoji_failure_does_not_block_confirmation()
    {
        var (tracker, slack, _) = Tracker(sentEmoji: "teams-relay");
        slack.RespondToBody = (method, body) =>
            method == "reactions.add" && body.GetProperty("name").GetString() == "teams-relay" ? """{"ok":false,"error":"internal_error"}""" : null;
        var send = tracker.Register(ClientMessageId, "C1", Ts, inThread: true);

        await tracker.ConfirmAsync(send, null, null, TestContext.Current.CancellationToken);

        Assert.True(send.Confirmed);
        Assert.Contains(slack.Requests, r => r.Method == "reactions.remove" && r.Body.GetProperty("name").GetString() == "hourglass_flowing_sand");
    }

    private static MessageDto Echo() =>
        new("1793220563903", "19:a@thread.v2", "8:me", "Me", "RichText/Html", "<p>hi</p>", 1, SentAt.AddSeconds(30), 1793220563903,
            ClientMessageId: ClientMessageId);

    private static (SendTracker Tracker, RecordingSlack Slack, MessageLinks Links) Tracker(string? appToken = "xapp-1", string sentEmoji = "outbox_tray")
    {
        var slack = new RecordingSlack();
        var links = new MessageLinks();
        var options = new RelayOptions { Slack = { AppToken = appToken, SentEmoji = sentEmoji } };
        return (RelayFakes.Sends(slack, links, options), slack, links);
    }
}
