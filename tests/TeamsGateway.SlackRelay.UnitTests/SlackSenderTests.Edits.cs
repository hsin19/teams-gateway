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
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, Page(TeamsId, ClientMessageId));

        await s.Sender.OnActionAsync(Click("update", $"C1|{Ts}|"), Ct);

        var put = Assert.Single(s.Gateway.Requests, r => r.Method == "PUT");
        Assert.Equal($"/v1/chats/{Chat}/messages/{TeamsId}", put.Path);
        var body = JsonDocument.Parse(put.Body).RootElement;
        Assert.Equal("<p>hello</p>", body.GetProperty("html").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("replyToId").ValueKind);
        Assert.Contains("Updated in Teams", Responses(s)[^1].GetProperty("text").GetString());
    }

    // 頂層發的那則改過去之後 Teams 會推回來：Slack 上本來就是新的了，不再貼「Edited in Teams」
    [Fact]
    public async Task Update_from_slack_is_not_echoed_back_as_a_teams_edit()
    {
        var s = Create(message: Message(reactions: SentReaction));
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, Page(TeamsId, ClientMessageId));
        s.Links.Remember(TeamsId, "C1", Ts, "", null, chat: Chat, byBot: false);

        await s.Sender.OnActionAsync(Click("update", $"C1|{Ts}|"), Ct);
        await s.Worker.RelayWithRetryAsync(Pushed(edited: true), new() { [Chat] = "C1" }, null, Ct);

        Assert.DoesNotContain(s.Slack.Requests, r => r.Method == "chat.postMessage");
    }

    // thread 裡發的是引用回覆：Teams 的編輯整則換掉，要再帶引用，不然變成普通訊息
    [Fact]
    public async Task Update_of_a_quote_reply_keeps_the_quote()
    {
        var s = Create();
        var read = s.Slack.Respond!;
        s.Slack.Respond = uri => uri.AbsolutePath.EndsWith("conversations.replies", StringComparison.Ordinal)
            ? $$"""{"ok":true,"messages":[{"ts":"{{ThreadTs}}","bot_id":"B1","text":"parent"},{{Message(reactions: SentReaction).Replace("\"user\":\"UME\"", $"\"user\":\"UME\",\"thread_ts\":\"{ThreadTs}\"", StringComparison.Ordinal)}}]}"""
            : read(uri);
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, Page(TeamsId, ClientMessageId));

        await s.Sender.OnActionAsync(Click("update", $"C1|{Ts}|{ThreadTs}"), Ct);

        var put = Assert.Single(s.Gateway.Requests, r => r.Method == "PUT");
        Assert.Equal("1793210000000", JsonDocument.Parse(put.Body).RootElement.GetProperty("replyToId").GetString());
    }

    // Teams 上已經刪了（409）：重試也沒用，直接說明
    [Fact]
    public async Task Update_of_a_message_deleted_in_teams_says_so()
    {
        var s = Create(message: Message(reactions: SentReaction));
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, Page(TeamsId, ClientMessageId));
        s.Gateway.Responses[$"PUT /v1/chats/{Chat}/messages/{TeamsId}"] = (HttpStatusCode.Conflict, """{"error":"deleted"}""");

        await s.Sender.OnActionAsync(Click("update", $"C1|{Ts}|"), Ct);

        Assert.Contains("deleted in Teams", Responses(s)[^1].GetProperty("text").GetString());
    }

    // 其他失敗：改是重送安全的，給 Retry（還是 Update）；推回來的就不算自己改的了
    [Fact]
    public async Task Update_failure_offers_a_retry()
    {
        var s = Create(message: Message(reactions: SentReaction));
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, Page(TeamsId, ClientMessageId));
        s.Gateway.Responses[$"PUT /v1/chats/{Chat}/messages/{TeamsId}"] = (HttpStatusCode.BadGateway, "{}");
        s.Links.Remember(TeamsId, "C1", Ts, "", null, chat: Chat, byBot: false);

        await s.Sender.OnActionAsync(Click("update", $"C1|{Ts}|"), Ct);
        await s.Worker.RelayWithRetryAsync(Pushed(edited: true), new() { [Chat] = "C1" }, null, Ct);

        var last = Responses(s)[^1];
        Assert.Equal("update", last.GetProperty("blocks")[1].GetProperty("elements")[0].GetProperty("action_id").GetString());
        Assert.Single(s.Slack.Requests, r => r.Method == "chat.postMessage"); // Teams 上的編輯照常貼進 thread
    }

    // 送出後在 Slack 上刪了：問要不要也刪 Teams 上那則；頂層的選 Keep 會補貼回來
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
        Assert.Equal(["delete", "keep_deleted"], ActionIds(ephemeral));
        Assert.DoesNotContain(s.Gateway.Requests, r => r.Method == "DELETE");
    }

    // 有回覆的頂層訊息被刪：Slack 送 message_changed、新版是 tombstone，一樣當成刪除
    [Fact]
    public async Task Deleted_thread_parent_counts_as_a_delete()
    {
        var s = Create();

        await s.Sender.OnMessageChangedAsync(new SlackEvent("message", null, null, "1729834600.000100", "C1", Subtype: "message_changed",
            Message: new HistoryMessage(Ts, "This message was deleted.", null, Subtype: "tombstone"),
            PreviousMessage: new HistoryMessage(Ts, "<@UBOT> hello", null, User: "UME", Reactions: [new SlackReaction("outbox_tray", ["UBOT"], 1)])), Ct);

        var ephemeral = Assert.Single(s.Slack.Requests, r => r.Method == "chat.postEphemeral").Body;
        Assert.Equal(["delete", "keep_deleted"], ActionIds(ephemeral));
    }

    // 刪除事件沒附表情時：開頭是 @bot 的到 Teams 查一次，送過才問
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_without_reactions_is_checked_in_teams_first(bool inTeams)
    {
        var s = Create();
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, inTeams ? Page(TeamsId, ClientMessageId) : """{"messages":[]}""");

        await s.Sender.OnMessageChangedAsync(new SlackEvent("message", null, null, "1729834600.000100", "C1", Subtype: "message_deleted",
            PreviousMessage: new HistoryMessage(Ts, "<@UBOT> hello", null, User: "UME"), DeletedTs: Ts), Ct);

        Assert.Equal(inTeams ? 1 : 0, s.Slack.Requests.Count(r => r.Method == "chat.postEphemeral"));
    }

    // thread 裡發的那則被刪：頂層有 bot 貼的那則，Keep 只是收掉確認
    [Fact]
    public async Task Deleted_thread_send_keeps_without_posting_back()
    {
        var s = Create();

        await s.Sender.OnMessageChangedAsync(new SlackEvent("message", null, null, "1729834600.000100", "C1", Subtype: "message_deleted",
            PreviousMessage: new HistoryMessage(Ts, "<@UBOT> hello", null, User: "UME", ThreadTs: ThreadTs, Reactions: [new SlackReaction("outbox_tray", ["UBOT"], 1)]),
            DeletedTs: Ts), Ct);

        var ephemeral = Assert.Single(s.Slack.Requests, r => r.Method == "chat.postEphemeral").Body;
        Assert.Equal(["delete", "cancel"], ActionIds(ephemeral));
        Assert.Equal(ThreadTs, ephemeral.GetProperty("thread_ts").GetString());
    }

    // 按 Delete：刪 Teams 上那則；推回來的刪除不再貼「Deleted in Teams」
    [Fact]
    public async Task Delete_removes_the_message_from_teams()
    {
        var s = Create();
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, Page(TeamsId, ClientMessageId));
        s.Links.Remember(TeamsId, "C1", Ts, "", null, chat: Chat, byBot: false);

        await s.Sender.OnActionAsync(Click("delete", $"C1|{Ts}|"), Ct);
        await s.Worker.RelayWithRetryAsync(Pushed(deleted: true), new() { [Chat] = "C1" }, null, Ct);

        var delete = Assert.Single(s.Gateway.Requests, r => r.Method == "DELETE");
        Assert.Equal($"/v1/chats/{Chat}/messages/{TeamsId}", delete.Path);
        Assert.Contains("Deleted in Teams", Responses(s)[^1].GetProperty("text").GetString());
        Assert.DoesNotContain(s.Slack.Requests, r => r.Method == "chat.postMessage");
    }

    // 頂層發的那則在 Slack 上刪了、留在 Teams：補貼成一般的轉送，之後 Teams 上的編輯原地更新它
    [Fact]
    public async Task Keep_after_delete_posts_it_back_to_the_channel()
    {
        var s = Create();
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, Page(TeamsId, ClientMessageId));

        await s.Sender.OnActionAsync(Click("keep_deleted", $"C1|{Ts}|"), Ct);

        var post = Assert.Single(s.Slack.Requests, r => r.Method == "chat.postMessage").Body;
        Assert.Contains("hello", post.GetProperty("text").GetString());
        Assert.Equal(TeamsId, post.GetProperty("metadata").GetProperty("event_payload").GetProperty("teams_id").GetString());
        Assert.True(s.Links.Lookup(TeamsId)?.ByBot);
        Assert.DoesNotContain(s.Gateway.Requests, r => r.Method == "DELETE");
        Assert.Contains("posted back", Responses(s)[^1].GetProperty("text").GetString());
    }

    // Teams 上已經沒有了：Delete、Keep 都沒事可做
    [Theory]
    [InlineData("delete", "nothing to delete")]
    [InlineData("keep_deleted", "nothing to post back")]
    public async Task Delete_or_keep_of_a_message_missing_from_teams_says_so(string action, string text)
    {
        var s = Create();
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, """{"messages":[]}""");

        await s.Sender.OnActionAsync(Click(action, $"C1|{Ts}|"), Ct);

        Assert.DoesNotContain(s.Gateway.Requests, r => r.Method == "DELETE");
        Assert.DoesNotContain(s.Slack.Requests, r => r.Method == "chat.postMessage");
        Assert.Contains(text, Responses(s)[^1].GetProperty("text").GetString());
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

    private static string Page(string id, string clientMessageId) => $$"""
        {"messages":[{"id":"{{id}}","conversationId":"{{Chat}}","from":"8:me","fromName":"Me","type":"RichText/Html","content":"<p>hello</p>",
          "sequenceId":1,"time":"2026-10-29T00:00:00Z","version":{{id}},"clientMessageId":"{{clientMessageId}}"}]}
        """;

    /// <summary>改 / 刪之後 Teams 推回來的那則。</summary>
    private static TeamsGateway.Contracts.MessageDto Pushed(bool edited = false, bool deleted = false) =>
        new(TeamsId, Chat, "8:me", "Me", "RichText/Html", deleted ? "" : "<p>fixed</p>", 1, DateTimeOffset.FromUnixTimeSeconds(1729834567 + 30),
            1793220599999, Edited: edited, Deleted: deleted, ClientMessageId: ClientMessageId);
}
