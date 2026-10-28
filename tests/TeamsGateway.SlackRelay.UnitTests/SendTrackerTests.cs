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
        Assert.Equal(["teams-relay", "outbox_tray", "outbox_tray"], added);
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
