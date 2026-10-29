using System.Net;
using System.Text;
using System.Text.Json;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>送到 Teams 之後在 Slack 上改、刪（ADR 0013）：問過才同步，Update / Delete / Keep。</summary>
public partial class SlackSenderTests
{
    private const string SentReaction = """[{"name":"outbox_tray","users":["UBOT"],"count":1}]""";

    // 送出後在 Slack 上改了：不自動同步，只問本人要不要也改 Teams 上那則
    [Fact]
    public async Task Edit_of_a_sent_message_offers_to_update_teams()
    {
        var s = Create(message: Message(reactions: SentReaction));

        await s.Sender.HandleAsync(Envelope("""
            {"type":"message","subtype":"message_changed","channel":"C1","hidden":true,"ts":"1729834600.000100",
             "message":{"type":"message","user":"UME","text":"<@UBOT> fixed","ts":"1729834567.123456","edited":{"user":"UME","ts":"1729834600.000000"}},
             "previous_message":{"type":"message","user":"UME","text":"<@UBOT> hello","ts":"1729834567.123456"}}
            """), Ct);

        var ephemeral = Assert.Single(s.Slack.Requests, r => r.Method == "chat.postEphemeral").Body;
        Assert.Equal("UME", ephemeral.GetProperty("user").GetString());
        Assert.Equal(["update", "cancel"], ActionIds(ephemeral));
        Assert.DoesNotContain(s.Gateway.Requests, r => r.Method == "PUT");
    }

    // 還沒按 Send 就改了：按 Send 時本來就讀最新的，不用問
    [Fact]
    public async Task Edit_of_a_message_not_yet_sent_is_ignored()
    {
        var s = Create();

        await s.Sender.OnMessageChangedAsync(Edited(), Ct);

        Assert.DoesNotContain(s.Slack.Requests, r => r.Method == "chat.postEphemeral");
    }

    // 沒有 edited 的 message_changed 是 Slack 自己補的（連結預覽之類），內容沒變；不在白名單的人改的也不管
    [Theory]
    [InlineData(false, "UME")]
    [InlineData(true, "UOTHER")]
    public async Task Change_that_is_not_a_senders_edit_is_ignored(bool edited, string user)
    {
        var s = Create(message: Message(reactions: SentReaction));
        var evt = Edited(user);

        await s.Sender.OnMessageChangedAsync(edited ? evt : evt with { Message = evt.Message! with { Edited = null } }, Ct);

        Assert.DoesNotContain(s.Slack.Requests, r => r.Method is "chat.postEphemeral" or "conversations.history");
    }

    // 按 Update：重讀那則、整則換掉 Teams 上那則（用 clientMessageId 找到它）
    [Fact]
    public async Task Update_replaces_the_message_in_teams()
    {
        var s = Create(message: Message(reactions: SentReaction));
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, Page("<p>hello</p>"));

        await s.Sender.OnActionAsync(Click("update", $"C1|{Ts}|"), Ct);

        var put = Assert.Single(s.Gateway.Requests, r => r.Method == "PUT");
        Assert.Equal($"/v1/chats/{Chat}/messages/{TeamsId}", put.Path);
        var body = JsonDocument.Parse(put.Body).RootElement;
        Assert.Equal("<p>hello</p>", body.GetProperty("html").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("replyToId").ValueKind);
        Assert.Contains("Updated in Teams", Responses(s)[^1].GetProperty("text").GetString());
    }

    // 頂層發的那則改過去之後 Teams 會推回來：Slack 上本來就是這個樣子，不再貼「Edited in Teams」
    [Fact]
    public async Task Update_from_slack_is_not_echoed_back_as_a_teams_edit()
    {
        var s = Create(message: Message(reactions: SentReaction));
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, Page("<p>old</p>"));
        s.Links.Remember(TeamsId, "C1", Ts, "", null, chat: Chat, byBot: false);

        await s.Sender.OnActionAsync(Click("update", $"C1|{Ts}|"), Ct);
        var sentHtml = JsonDocument.Parse(Assert.Single(s.Gateway.Requests, r => r.Method == "PUT").Body).RootElement.GetProperty("html").GetString();
        await s.Worker.RelayWithRetryAsync(Pushed(sentHtml + "\r\n"), new() { [Chat] = "C1" }, null, Ct);

        Assert.DoesNotContain(s.Slack.Requests, r => r.Method == "chat.postMessage");
    }

    // 引用看 Teams 上那則現在引用誰，不從 Slack 的 thread 上層推（上層可能已經刪了）：Teams 的編輯整則換掉，不再帶就變成普通訊息
    [Fact]
    public async Task Update_keeps_the_quote_the_teams_message_has()
    {
        var s = Create(message: Message(reactions: SentReaction));
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, Page(
            """<blockquote itemscope itemtype=\"http://schema.skype.com/Reply\" itemid=\"1793210000000\"><p itemprop=\"preview\">earlier</p></blockquote><p>hello</p>"""));

        await s.Sender.OnActionAsync(Click("update", $"C1|{Ts}|"), Ct);

        var put = Assert.Single(s.Gateway.Requests, r => r.Method == "PUT");
        Assert.Equal("1793210000000", JsonDocument.Parse(put.Body).RootElement.GetProperty("replyToId").GetString());
    }

    // Teams 上已經刪了（409）：重試也沒用，直接說明
    [Fact]
    public async Task Update_of_a_message_deleted_in_teams_says_so()
    {
        var s = Create(message: Message(reactions: SentReaction));
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, Page("<p>hello</p>"));
        s.Gateway.Responses[$"PUT /v1/chats/{Chat}/messages/{TeamsId}"] = (HttpStatusCode.Conflict, """{"error":"deleted"}""");

        await s.Sender.OnActionAsync(Click("update", $"C1|{Ts}|"), Ct);

        Assert.Contains("deleted in Teams", Responses(s)[^1].GetProperty("text").GetString());
    }

    // 連不上、5xx：改是重送安全的，給 Retry（還是 Update）
    [Fact]
    public async Task Update_failure_offers_a_retry()
    {
        var s = Create(message: Message(reactions: SentReaction));
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, Page("<p>hello</p>"));
        s.Gateway.Responses[$"PUT /v1/chats/{Chat}/messages/{TeamsId}"] = (HttpStatusCode.BadGateway, "{}");

        await s.Sender.OnActionAsync(Click("update", $"C1|{Ts}|"), Ct);

        var last = Responses(s)[^1];
        Assert.Equal("update", last.GetProperty("blocks")[1].GetProperty("elements")[0].GetProperty("action_id").GetString());
    }

    // gateway 還沒有這個端點（舊版，404 不是它回的「沒有這則」）、或請求被拒（400）：不是「Teams 上沒有了」，重試也沒用
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"replyToId must be a message in this chat that the gateway has received"}""")]
    public async Task Update_refused_by_the_gateway_is_reported_without_a_retry(HttpStatusCode status, string body)
    {
        var s = Create(message: Message(reactions: SentReaction));
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, Page("<p>hello</p>"));
        s.Gateway.Responses[$"PUT /v1/chats/{Chat}/messages/{TeamsId}"] = (status, body);

        await s.Sender.OnActionAsync(Click("update", $"C1|{Ts}|"), Ct);

        var last = Responses(s)[^1];
        Assert.Contains("refused", last.GetProperty("text").GetString());
        Assert.False(last.TryGetProperty("blocks", out _));
    }

    // 送出後在 Slack 上刪了：問要不要也刪 Teams 上那則
    [Fact]
    public async Task Delete_of_a_sent_message_offers_to_delete_in_teams()
    {
        var s = Create();

        await s.Sender.HandleAsync(Envelope("""
            {"type":"message","subtype":"message_deleted","channel":"C1","hidden":true,"ts":"1729834600.000100","deleted_ts":"1729834567.123456",
             "previous_message":{"type":"message","user":"UME","text":"<@UBOT> hello","ts":"1729834567.123456",
               "reactions":[{"name":"outbox_tray","users":["UBOT"],"count":1}]}}
            """), Ct);

        var ephemeral = Assert.Single(s.Slack.Requests, r => r.Method == "chat.postEphemeral").Body;
        Assert.Equal(["delete", "cancel"], ActionIds(ephemeral));
        Assert.DoesNotContain(s.Gateway.Requests, r => r.Method == "DELETE");
    }

    // 有回覆的頂層訊息被刪：Slack 送 message_changed、新版是 tombstone，一樣當成刪除；之後那則 thread 有動靜再送的（舊版也是 tombstone）不再問
    [Fact]
    public async Task Deleted_thread_parent_counts_as_a_delete_once()
    {
        var s = Create();
        var tombstone = new HistoryMessage(Ts, "This message was deleted.", null, User: "USLACKBOT", Subtype: "tombstone");

        await s.Sender.OnMessageChangedAsync(new SlackEvent("message", null, null, "1729834600.000100", "C1", Subtype: "message_changed",
            Message: tombstone,
            PreviousMessage: new HistoryMessage(Ts, "<@UBOT> hello", null, User: "UME", Reactions: [new SlackReaction("outbox_tray", ["UBOT"], 1)])), Ct);
        await s.Sender.OnMessageChangedAsync(new SlackEvent("message", null, null, "1729834700.000100", "C1", Subtype: "message_changed",
            Message: tombstone, PreviousMessage: tombstone with { User = "UME" }), Ct);

        var ephemeral = Assert.Single(s.Slack.Requests, r => r.Method == "chat.postEphemeral").Body;
        Assert.Equal(["delete", "cancel"], ActionIds(ephemeral));
    }

    // 刪除事件沒附表情時：開頭是 @bot 的到 Teams 查一次，送過才問
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_without_reactions_is_checked_in_teams_first(bool inTeams)
    {
        var s = Create();
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, inTeams ? Page("<p>hello</p>") : """{"messages":[]}""");

        await s.Sender.OnMessageChangedAsync(new SlackEvent("message", null, null, "1729834600.000100", "C1", Subtype: "message_deleted",
            PreviousMessage: new HistoryMessage(Ts, "<@UBOT> hello", null, User: "UME"), DeletedTs: Ts), Ct);

        Assert.Equal(inTeams ? 1 : 0, s.Slack.Requests.Count(r => r.Method == "chat.postEphemeral"));
    }

    // thread 裡發的那則被刪：確認也貼在那個 thread
    [Fact]
    public async Task Deleted_thread_send_is_asked_about_in_its_thread()
    {
        var s = Create();

        await s.Sender.OnMessageChangedAsync(new SlackEvent("message", null, null, "1729834600.000100", "C1", Subtype: "message_deleted",
            PreviousMessage: new HistoryMessage(Ts, "<@UBOT> hello", null, User: "UME", ThreadTs: ThreadTs, Reactions: [new SlackReaction("outbox_tray", ["UBOT"], 1)]),
            DeletedTs: Ts), Ct);

        var ephemeral = Assert.Single(s.Slack.Requests, r => r.Method == "chat.postEphemeral").Body;
        Assert.Equal(ThreadTs, ephemeral.GetProperty("thread_ts").GetString());
    }

    // 按 Delete：刪 Teams 上那則；推回來的刪除不再貼「Deleted in Teams」（Slack 上那則也刪了）
    [Fact]
    public async Task Delete_removes_the_message_from_teams()
    {
        var s = Create();
        var read = s.Slack.Respond!;
        s.Slack.Respond = uri => uri.AbsolutePath.EndsWith("conversations.history", StringComparison.Ordinal) && uri.Query.Contains($"oldest={Ts}", StringComparison.Ordinal)
            ? """{"ok":true,"messages":[]}"""
            : read(uri);
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, Page("<p>hello</p>"));
        s.Links.Remember(TeamsId, "C1", Ts, "", null, chat: Chat, byBot: false);

        await s.Sender.OnActionAsync(Click("delete", $"C1|{Ts}|"), Ct);
        await s.Worker.RelayWithRetryAsync(Pushed("", deleted: true), new() { [Chat] = "C1" }, null, Ct);

        var delete = Assert.Single(s.Gateway.Requests, r => r.Method == "DELETE");
        Assert.Equal($"/v1/chats/{Chat}/messages/{TeamsId}", delete.Path);
        Assert.Contains("Deleted in Teams", Responses(s)[^1].GetProperty("text").GetString());
        Assert.DoesNotContain(s.Slack.Requests, r => r.Method == "chat.postMessage");
    }

    // Teams 上已經沒有了：沒事可做
    [Fact]
    public async Task Delete_of_a_message_missing_from_teams_says_so()
    {
        var s = Create();
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, """{"messages":[]}""");

        await s.Sender.OnActionAsync(Click("delete", $"C1|{Ts}|"), Ct);

        Assert.DoesNotContain(s.Gateway.Requests, r => r.Method == "DELETE");
        Assert.Contains("nothing to delete", Responses(s)[^1].GetProperty("text").GetString());
    }

    private static SlackEvent Edited(string user = "UME") =>
        new("message", null, null, "1729834600.000100", "C1", Subtype: "message_changed",
            Message: new HistoryMessage(Ts, "<@UBOT> fixed", null, User: user, Edited: new SlackEdited(user, "1729834600.000000")),
            PreviousMessage: new HistoryMessage(Ts, "<@UBOT> hello", null, User: user));

    private static SocketEnvelope Envelope(string evt) =>
        SlackSocketMode.Parse(Encoding.UTF8.GetBytes(
            $$$"""{"envelope_id":"e1","type":"events_api","payload":{"type":"event_callback","event_id":"Ev1","event":{{{evt}}}}}"""))!;

    private static string[] ActionIds(JsonElement ephemeral) =>
        ephemeral.GetProperty("blocks")[1].GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("action_id").GetString()!).ToArray();

    /// <summary>gateway 讀回來的一頁：從 Slack 發過去的那則（Teams id 與 clientMessageId 對得上），內容是 <paramref name="contentJson"/>（已跳脫成 JSON 字串內容）。</summary>
    private static string Page(string contentJson) => $$"""
        {"messages":[{"id":"{{TeamsId}}","conversationId":"{{Chat}}","from":"8:me","fromName":"Me","type":"RichText/Html","content":"{{contentJson}}",
          "sequenceId":1,"time":"2026-10-29T00:00:00Z","version":{{TeamsId}},"clientMessageId":"{{ClientMessageId}}"}]}
        """;

    /// <summary>改 / 刪之後 Teams 推回來的那則。</summary>
    private static TeamsGateway.Contracts.MessageDto Pushed(string? content, bool deleted = false) =>
        new(TeamsId, Chat, "8:me", "Me", "RichText/Html", content, 1, DateTimeOffset.FromUnixTimeSeconds(1729834567 + 30),
            1793220599999, Edited: !deleted, Deleted: deleted, ClientMessageId: ClientMessageId);
}
