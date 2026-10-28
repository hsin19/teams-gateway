using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsGateway.Contracts;
using Microsoft.Extensions.Options;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>Slack → Teams 發話的流程：@bot → 確認 → Send / Cancel / Retry（ADR 0012）。Slack 與 gateway 都是假的。</summary>
public class SlackSenderTests
{
    private const string Chat = "19:a@thread.v2";
    private const string Ts = "1729834567.123456";
    private const string ClientMessageId = "1729834567123456";
    private const string ThreadTs = "1729830000.000100";
    private const string TeamsId = "1793220563903";
    private const string ResponseUrl = "https://hooks.slack.com/actions/T1/1/respond";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // @bot 在句子中間是在討論 bot 本身：不理
    [Fact]
    public async Task Mention_in_the_middle_is_ignored()
    {
        var s = Create();

        await s.Sender.OnMentionAsync(new SlackEvent("app_mention", "UME", "hey <@UBOT> look", Ts, "C1"), Ct);

        Assert.DoesNotContain(s.Slack.Requests, r => r.Method == "chat.postEphemeral");
    }

    // 不在白名單的人：說明，不給按鈕
    [Fact]
    public async Task Someone_not_on_the_list_is_told_so()
    {
        var s = Create();

        await s.Sender.OnMentionAsync(new SlackEvent("app_mention", "UOTHER", "<@UBOT> hi", Ts, "C1"), Ct);

        var ephemeral = Assert.Single(s.Slack.Requests, r => r.Method == "chat.postEphemeral").Body;
        Assert.Equal("UOTHER", ephemeral.GetProperty("user").GetString());
        Assert.Contains("not on the list", ephemeral.GetProperty("text").GetString());
        Assert.False(ephemeral.TryGetProperty("blocks", out _));
    }

    // 頂層、頻道只對到一個對話：確認寫明群名與預覽，按鈕只帶頻道與 ts
    [Fact]
    public async Task Top_level_mention_asks_for_confirmation()
    {
        var s = Create();

        await s.Sender.OnMentionAsync(new SlackEvent("app_mention", "UME", "<@UBOT> hello", Ts, "C1"), Ct);

        var blocks = Assert.Single(s.Slack.Requests, r => r.Method == "chat.postEphemeral").Body.GetProperty("blocks");
        var text = blocks[0].GetProperty("text").GetProperty("text").GetString();
        Assert.Contains("Team A", text);
        Assert.Contains("> hello", text);
        var buttons = blocks[1].GetProperty("elements");
        Assert.Equal(("send", "C1|1729834567.123456|"), (buttons[0].GetProperty("action_id").GetString(), buttons[0].GetProperty("value").GetString()));
        Assert.Equal("cancel", buttons[1].GetProperty("action_id").GetString());
    }

    // DefaultChannel 收所有對話：頂層不知道送哪個，請改在 thread 裡發
    [Fact]
    public async Task Top_level_in_the_default_channel_cannot_send()
    {
        var s = Create(slack =>
        {
            slack.Routes.Clear();
            slack.DefaultChannel = "C1";
        });

        await s.Sender.OnMentionAsync(new SlackEvent("app_mention", "UME", "<@UBOT> hello", Ts, "C1"), Ct);

        var ephemeral = Assert.Single(s.Slack.Requests, r => r.Method == "chat.postEphemeral").Body;
        Assert.Contains("Reply in the thread", ephemeral.GetProperty("text").GetString());
    }

    // 按 Send：重讀那則、轉成 HTML、clientMessageId 是 ts 去掉小數點；⏳ → 已送出，確認收掉，頂層那則記進對應表
    [Fact]
    public async Task Send_posts_html_and_marks_the_message_sent()
    {
        var s = Create();

        await s.Sender.OnActionAsync(Click("send", $"C1|{Ts}|"), Ct);

        var post = Assert.Single(s.Gateway.Requests, r => r.Method == "POST");
        Assert.Equal($"/v1/chats/{Chat}/messages", post.Path);
        var body = JsonDocument.Parse(post.Body).RootElement;
        Assert.Equal("<p>hello</p>", body.GetProperty("html").GetString());
        Assert.Equal(ClientMessageId, body.GetProperty("clientMessageId").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("replyToId").ValueKind);

        Assert.Equal(["hourglass_flowing_sand", "outbox_tray"], Reactions(s, "reactions.add"));
        Assert.True(Responses(s)[^1].GetProperty("delete_original").GetBoolean());
        var link = s.Links.Lookup(TeamsId);
        Assert.Equal(("C1", Ts, false), (link?.Channel, link?.Ts, link?.ByBot));
    }

    // thread 裡、上層是轉過來的 Teams 訊息：送到 metadata 裡的對話，當成那則的引用回覆（頻道對到多個對話也行）
    [Fact]
    public async Task Reply_in_a_teams_thread_becomes_a_quote_reply()
    {
        var s = Create(slack => slack.Routes.Add(new RelayOptions.ChannelRoute { Chat = "19:b@thread.v2", Channel = "C1" }));

        await s.Sender.OnActionAsync(Click("send", $"C1|{Ts}|{ThreadTs}"), Ct);

        var post = Assert.Single(s.Gateway.Requests, r => r.Method == "POST");
        Assert.Equal($"/v1/chats/{Chat}/messages", post.Path);
        Assert.Equal("1793210000000", JsonDocument.Parse(post.Body).RootElement.GetProperty("replyToId").GetString());
        Assert.Null(s.Links.Lookup(TeamsId)); // thread 裡發的推回來時由 bot 貼，照 metadata 對應
    }

    // 確認之後那則被刪了：Slack 會回別則，核對 ts 後不送
    [Fact]
    public async Task Deleted_message_is_not_sent()
    {
        var s = Create(message: Message(ts: "1729834500.000001"));

        await s.Sender.OnActionAsync(Click("send", $"C1|{Ts}|"), Ct);

        Assert.DoesNotContain(s.Gateway.Requests, r => r.Method == "POST");
        Assert.Contains("gone", Responses(s)[^1].GetProperty("text").GetString());
    }

    // 已經有 bot 的已送出表情（連點、重送事件）：不再送
    [Fact]
    public async Task Already_sent_message_is_not_sent_again()
    {
        var s = Create(message: Message(reactions: """[{"name":"outbox_tray","users":["UBOT"],"count":1}]"""));

        await s.Sender.OnActionAsync(Click("send", $"C1|{Ts}|"), Ct);

        Assert.DoesNotContain(s.Gateway.Requests, r => r.Method == "POST");
        Assert.Contains("already sent", Responses(s)[^1].GetProperty("text").GetString());
    }

    // 停在 ⏳ 卻沒人在送（上次送到一半 relay 重啟）：先到對話裡找，已經在就補成已送出、不再送
    [Fact]
    public async Task Leftover_hourglass_that_reached_teams_is_not_sent_again()
    {
        var s = Create(message: Message(reactions: """[{"name":"hourglass_flowing_sand","users":["UBOT"],"count":1}]"""));
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, $$"""
            {"messages":[{"id":"{{TeamsId}}","conversationId":"{{Chat}}","clientMessageId":"{{ClientMessageId}}","sequenceId":1,"time":"2026-10-29T00:00:00Z"}]}
            """);

        await s.Sender.OnActionAsync(Click("send", $"C1|{Ts}|"), Ct);

        Assert.DoesNotContain(s.Gateway.Requests, r => r.Method == "POST");
        Assert.Equal(["outbox_tray"], Reactions(s, "reactions.add"));
        Assert.Contains("already sent", Responses(s)[^1].GetProperty("text").GetString());
    }

    // 停在 ⏳、對話裡也沒有：上次根本沒送出去，照常送
    [Fact]
    public async Task Leftover_hourglass_that_never_reached_teams_is_sent()
    {
        var s = Create(message: Message(reactions: """[{"name":"hourglass_flowing_sand","users":["UBOT"],"count":1}]"""));
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, """{"messages":[]}""");

        await s.Sender.OnActionAsync(Click("send", $"C1|{Ts}|"), Ct);

        Assert.Single(s.Gateway.Requests, r => r.Method == "POST");
    }

    // 別人按（不在白名單）：不送
    [Fact]
    public async Task Click_by_someone_not_on_the_list_sends_nothing()
    {
        var s = Create();

        await s.Sender.OnActionAsync(Click("send", $"C1|{Ts}|", user: "UOTHER"), Ct);

        Assert.DoesNotContain(s.Gateway.Requests, r => r.Method == "POST");
    }

    // gateway 回 4xx：確定沒送，⚠️ 加 Retry
    [Fact]
    public async Task Refused_send_offers_a_retry()
    {
        var s = Create();
        s.Gateway.Responses[$"POST /v1/chats/{Chat}/messages"] = (HttpStatusCode.BadRequest, """{"error":"nope"}""");

        await s.Sender.OnActionAsync(Click("send", $"C1|{Ts}|"), Ct);

        Assert.Equal(["hourglass_flowing_sand", "warning"], Reactions(s, "reactions.add"));
        var last = Responses(s)[^1];
        Assert.Contains("refused", last.GetProperty("blocks")[0].GetProperty("text").GetProperty("text").GetString());
        Assert.Equal("retry", last.GetProperty("blocks")[1].GetProperty("elements")[0].GetProperty("action_id").GetString());
    }

    // gateway 沒給結果（5xx / 逾時）但對話裡已經有這個 clientMessageId：算送到了，不給 Retry（Teams 不去重，重送會變兩則）
    [Fact]
    public async Task Unanswered_send_found_in_the_chat_counts_as_sent()
    {
        var s = Create();
        s.Gateway.Responses[$"POST /v1/chats/{Chat}/messages"] = (HttpStatusCode.BadGateway, "");
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, $$"""
            {"messages":[{"id":"{{TeamsId}}","conversationId":"{{Chat}}","clientMessageId":"{{ClientMessageId}}","sequenceId":1,"time":"2026-10-29T00:00:00Z"}]}
            """);

        await s.Sender.OnActionAsync(Click("send", $"C1|{Ts}|"), Ct);

        Assert.Equal(["hourglass_flowing_sand", "outbox_tray"], Reactions(s, "reactions.add"));
        Assert.NotNull(s.Links.Lookup(TeamsId));
    }

    // 沒給結果、對話裡也沒有：可以放心重試
    [Fact]
    public async Task Unanswered_send_missing_from_the_chat_offers_a_safe_retry()
    {
        var s = Create();
        s.Gateway.Responses[$"POST /v1/chats/{Chat}/messages"] = (HttpStatusCode.BadGateway, "");
        s.Gateway.Responses[$"GET /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, """{"messages":[]}""");

        await s.Sender.OnActionAsync(Click("send", $"C1|{Ts}|"), Ct);

        Assert.Equal(["hourglass_flowing_sand", "warning"], Reactions(s, "reactions.add"));
        Assert.Contains("retrying is safe", Responses(s)[^1].GetProperty("blocks")[0].GetProperty("text").GetProperty("text").GetString());
    }

    // 查詢失敗時保留 ⏳；Retry 先重新查對話，不可直接再 POST
    [Fact]
    public async Task Unknown_send_outcome_is_preserved_and_retry_checks_before_posting()
    {
        var s = Create();
        s.Gateway.RespondAsync = (request, _) => Task.FromResult<(HttpStatusCode, string)?>(request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
            ? (HttpStatusCode.BadGateway, "")
            : null);
        var readHistory = s.Slack.Respond!;
        s.Slack.Respond = uri =>
        {
            var json = readHistory(uri);
            return uri.AbsolutePath.EndsWith("conversations.history", StringComparison.Ordinal)
                   && s.Slack.Requests.Any(r => r.Method == "reactions.add" && r.Body.GetProperty("name").GetString() == "hourglass_flowing_sand")
                ? json?.Replace("\"blocks\"", "\"reactions\":[{\"name\":\"hourglass_flowing_sand\",\"users\":[\"UBOT\"],\"count\":1}],\"blocks\"", StringComparison.Ordinal)
                : json;
        };

        await s.Sender.OnActionAsync(Click("send", $"C1|{Ts}|"), Ct);

        Assert.Single(s.Gateway.Requests, r => r.Method == "POST");
        Assert.DoesNotContain(s.Slack.Requests, r => r.Method == "reactions.remove" && r.Body.GetProperty("name").GetString() == "hourglass_flowing_sand");
        Assert.DoesNotContain("warning", Reactions(s, "reactions.add"));
        Assert.Contains("outcome unknown", Responses(s)[^1].GetProperty("text").GetString(), StringComparison.OrdinalIgnoreCase);

        await s.Sender.OnActionAsync(Click("retry", $"C1|{Ts}|"), Ct);

        Assert.Single(s.Gateway.Requests, r => r.Method == "POST");
        Assert.Contains("checking the chat failed", Responses(s)[^1].GetProperty("text").GetString());
    }

    // SSE 先確認成功時，稍後回來的空查詢不能把已送出的訊息降成失敗
    [Fact]
    public async Task Verification_cannot_downgrade_a_concurrently_confirmed_send()
    {
        var s = Create();
        var verificationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVerification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Gateway.RespondAsync = async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                    return (HttpStatusCode.BadGateway, "");
                verificationStarted.TrySetResult();
                await releaseVerification.Task.WaitAsync(ct);
                return (HttpStatusCode.OK, """{"messages":[]}""");
            }
            return null;
        };
        var sending = s.Sender.OnActionAsync(Click("send", $"C1|{Ts}|"), Ct);
        await verificationStarted.Task.WaitAsync(Ct);

        await s.Worker.RelayWithRetryAsync(
            new MessageDto(TeamsId, Chat, "8:me", "Me", "RichText/Html", "<p>hello</p>", 1,
                DateTimeOffset.FromUnixTimeSeconds(1729834597), long.Parse(TeamsId), ClientMessageId: ClientMessageId),
            new Dictionary<string, string> { [Chat] = "C1" }, null, Ct);
        releaseVerification.SetResult();
        await sending;

        Assert.DoesNotContain("warning", Reactions(s, "reactions.add"));
        Assert.True(Responses(s)[^1].GetProperty("delete_original").GetBoolean());
        Assert.NotNull(s.Links.Lookup(TeamsId));
    }

    // Cancel：收掉確認，什麼都不送
    [Fact]
    public async Task Cancel_removes_the_confirmation()
    {
        var s = Create();

        await s.Sender.OnActionAsync(Click("cancel", $"C1|{Ts}|"), Ct);

        Assert.Empty(s.Gateway.Requests);
        Assert.True(Assert.Single(Responses(s)).GetProperty("delete_original").GetBoolean());
    }

    // Socket Mode 沒及時 ack 會重送同一個事件：只回一次確認
    [Fact]
    public async Task Redelivered_event_is_handled_once()
    {
        var s = Create();
        var envelope = SlackSocketMode.Parse("""
            {"envelope_id":"e1","type":"events_api","payload":{"type":"event_callback","event_id":"Ev1",
              "event":{"type":"app_mention","user":"UME","text":"<@UBOT> hello","ts":"1729834567.123456","channel":"C1"}}}
            """u8)!;

        await s.Sender.HandleAsync(envelope, Ct);
        await s.Sender.HandleAsync(envelope with { RetryAttempt = 1 }, Ct);

        Assert.Single(s.Slack.Requests, r => r.Method == "chat.postEphemeral");
    }

    private static BlockActionsPayload Click(string action, string value, string user = "UME") =>
        new("block_actions", new SlackIdRef(user), new SlackIdRef("C1"), ResponseUrl, [new BlockAction(action, value)]);

    private static string Message(string ts = Ts, string? reactions = null) => $$"""
        {"ts":"{{ts}}","user":"UME","text":"<@UBOT> hello"{{(reactions is null ? "" : $",\"reactions\":{reactions}")}},
         "blocks":[{"type":"rich_text","elements":[{"type":"rich_text_section","elements":[{"type":"user","user_id":"UBOT"},{"type":"text","text":" hello"}]}]}]}
        """;

    private static string[] Reactions(Setup s, string method) =>
        s.Slack.Requests.Where(r => r.Method == method).Select(r => r.Body.GetProperty("name").GetString()!).ToArray();

    private static List<JsonElement> Responses(Setup s) => s.Slack.Requests.Where(r => r.Method == "respond").Select(r => r.Body).ToList();

    private sealed record Setup(SlackSender Sender, RecordingSlack Slack, FakeGateway Gateway, MessageLinks Links, RelayWorker Worker);

    private static Setup Create(Action<RelayOptions.SlackSettings>? configure = null, string? message = null)
    {
        var slack = new RecordingSlack();
        slack.Responses["auth.test"] = """{"ok":true,"user_id":"UBOT"}""";
        message ??= Message();
        var replyMessage = Message().Replace("\"user\":\"UME\"", $"\"user\":\"UME\",\"thread_ts\":\"{ThreadTs}\"", StringComparison.Ordinal);
        slack.Respond = uri =>
        {
            var query = uri.Query;
            if (uri.AbsolutePath.EndsWith("conversations.history", StringComparison.Ordinal) && query.Contains($"oldest={ThreadTs}", StringComparison.Ordinal))
                return $$$$"""
                    {"ok":true,"messages":[{"ts":"{{{{ThreadTs}}}}","bot_id":"B1","text":"*Bob*:\nhi",
                      "metadata":{"event_type":"teams_relay","event_payload":{"teams_id":"1793210000000","version":1793210000000,"chat":"{{{{Chat}}}}"}}}]}
                    """;
            if (uri.AbsolutePath.EndsWith("conversations.history", StringComparison.Ordinal))
                return $$"""{"ok":true,"messages":[{{message}}]}""";
            if (uri.AbsolutePath.EndsWith("conversations.replies", StringComparison.Ordinal))
                return $$"""{"ok":true,"messages":[{"ts":"{{ThreadTs}}","bot_id":"B1","text":"parent"},{{replyMessage}}]}""";
            return null;
        };

        var gateway = new FakeGateway();
        gateway.Responses["GET /v1/chats"] = (HttpStatusCode.OK, $$"""[{"id":"{{Chat}}","topic":"Team A"}]""");
        gateway.Responses[$"POST /v1/chats/{Chat}/messages"] = (HttpStatusCode.OK, $$"""{"clientMessageId":"{{ClientMessageId}}","messageId":"{{TeamsId}}"}""");

        var options = new RelayOptions
        {
            Slack =
            {
                BotToken = "xoxb-1",
                AppToken = "xapp-1",
                Senders = { "UME" },
                Routes = { new RelayOptions.ChannelRoute { Chat = Chat, Channel = "C1" } },
            },
        };
        configure?.Invoke(options.Slack);
        var links = new MessageLinks();
        var tracker = RelayFakes.Sends(slack, links, options);
        var worker = new RelayWorker(
            new SseEventSource(new GatewayEventStream(new HttpClient(), NullLogger<GatewayEventStream>.Instance), new MessageLinks(), NullLogger<SseEventSource>.Instance),
            RelayFakes.Slack(slack),
            new GatewayMediaClient(new HttpClient(), NullLogger<GatewayMediaClient>.Instance),
            links,
            tracker,
            Options.Create(options),
            NullLogger<RelayWorker>.Instance);
        var sender = new SlackSender(
            RelayFakes.Slack(slack),
            new GatewaySendClient(RelayFakes.Gateway(gateway), NullLogger<GatewaySendClient>.Instance),
            new GatewayStatusClient(RelayFakes.Gateway(gateway), NullLogger<GatewayStatusClient>.Instance),
            tracker,
            Options.Create(options),
            NullLogger<SlackSender>.Instance) { VerifyDelay = TimeSpan.Zero };
        return new Setup(sender, slack, gateway, links, worker);
    }
}
