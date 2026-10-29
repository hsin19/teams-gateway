using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// Slack → Teams 發話（ADR 0012）：訊息開頭 @bot → 只有本人看得到的確認 → 按 Send 才重讀那則、經 gateway 發出。
/// 事件由 <see cref="SlackSocketMode"/> 收進來、ack 完丟給 <see cref="HandleAsync"/> 在背景處理，所以同時可能有好幾個在跑。
/// 按鈕只帶著頻道與 ts，不記待確認的東西：relay 中間重啟，按鈕照樣有效。
/// 送出後在 Slack 上改 / 刪的同步在 <c>SlackSender.Edits.cs</c>（ADR 0013）。
/// </summary>
public sealed partial class SlackSender(
    SlackClient slack,
    GatewaySendClient gateway,
    GatewayStatusClient chats,
    SendTracker tracker,
    SlackBotIdentity bot,
    RelayWorker relay,
    IOptions<RelayOptions> options,
    ILogger<SlackSender> logger)
{
    internal const string SendAction = "send";
    internal const string CancelAction = "cancel";
    internal const string RetryAction = "retry";

    /// <summary>gateway 沒給明確結果時，等多久再去對話裡找那則有沒有送到（測試調短）。</summary>
    internal TimeSpan VerifyDelay { get; init; } = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan TopicsTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SeenEventsTtl = TimeSpan.FromMinutes(10);

    // 正在送的那幾則（頻道/ts）：擋幾乎同時的兩次點擊，只看表情擋不住
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    // Socket Mode 沒及時 ack 會重送同一個事件
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenEvents = new();
    private readonly ConcurrentDictionary<string, string> _userNames = new();
    private readonly ConcurrentDictionary<string, string> _channelNames = new();
    private (IReadOnlyDictionary<string, string?> Topics, DateTimeOffset At)? _topics;

    internal async Task HandleAsync(SocketEnvelope envelope, CancellationToken ct)
    {
        try
        {
            // message.channels 會送來頻道裡的每一則：只有改 / 刪要處理，其他的連去重表都不進
            if (envelope is { Type: "events_api", Payload: { } events }
                && events.Deserialize(SlackJsonContext.Default.EventCallback) is { Event: { } evt } callback
                && (evt.Type == "app_mention" || evt is { Type: "message", Subtype: MessageChanged or MessageDeleted })
                && FirstTime(callback.EventId))
            {
                if (evt.Type == "app_mention")
                    await OnMentionAsync(evt, ct);
                else
                    await OnMessageChangedAsync(evt, ct);
            }
            else if (envelope is { Type: "interactive", Payload: { } interaction }
                     && interaction.Deserialize(SlackJsonContext.Default.BlockActionsPayload) is { Type: "block_actions" } actions)
                await OnActionAsync(actions, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Handling a Slack {Type} event failed", envelope.Type);
        }
    }

    /// <summary>有人 @bot：開頭是 @bot、白名單裡的人、目標對話確定，才回確認；不然回 ephemeral 說明。</summary>
    internal async Task OnMentionAsync(SlackEvent mention, CancellationToken ct)
    {
        if (mention is not { User: { } user, Channel: { } channel, Ts: { } ts, BotId: null }
            || await tracker.BotUserIdAsync(ct) is not { } bot
            || !SlackRichText.StartsWithMention(mention.Text, bot))
            return;
        var threadTs = mention.ThreadTs is { } t && t != ts ? t : null;

        if (!IsSender(user))
        {
            await slack.PostEphemeralAsync(channel, user, NotASender, null, threadTs, ct);
            return;
        }
        SendTarget target;
        try
        {
            target = await ResolveTargetAsync(channel, threadTs, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 讀不到 thread 上層那則（或查不到它在 Teams 上是哪則）就不知道送去哪，不能退回頻道的規則亂猜
            logger.LogWarning(ex, "Resolving the Teams chat for {Channel}/{Ts} failed", channel, ts);
            await slack.PostEphemeralAsync(channel, user, $"Couldn't work out where to send this ({ex.Message}). @mention again to retry.", null, threadTs, ct);
            return;
        }
        if (target.Error is { } error)
        {
            await slack.PostEphemeralAsync(channel, user, error, null, threadTs, ct);
            return;
        }

        // 只是預覽：讀不到就用事件裡的文字，按 Send 時本來就會再讀一次
        HistoryMessage? message = null;
        try
        {
            message = await slack.GetMessageAsync(channel, ts, threadTs, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Reading {Channel}/{Ts} for the preview failed; using the event text", channel, ts);
        }

        var lines = new List<string>
        {
            $"*Send to Teams* → {Escape(await ChatLabelAsync(target.Chat!, ct))}{(target.ReplyToId is null ? "" : " (as a reply to this thread's Teams message)")}",
            SlackRichText.Preview(message?.Text ?? mention.Text, bot),
        };
        if (message?.Files is { Length: > 0 } files)
            lines.Add($"_Attachments aren't sent to Teams yet ({files.Length} file{(files.Length == 1 ? "" : "s")} left out)._");
        var value = Ref(channel, ts, threadTs);
        await slack.PostEphemeralAsync(channel, user, "Send this to Teams?",
            [SlackBlock.Section(string.Join('\n', lines)),
             SlackBlock.Actions(SlackButton.Of("Send", SendAction, value, "primary"), SlackButton.Of("Cancel", CancelAction, value))],
            threadTs, ct);
    }

    internal async Task OnActionAsync(BlockActionsPayload payload, CancellationToken ct)
    {
        if (payload is not { User.Id: { } user, ResponseUrl: { } responseUrl, Actions: [var action, ..] })
            return;
        if (action.ActionId == CancelAction)
        {
            await slack.RespondAsync(responseUrl, new ResponseUrlRequest(DeleteOriginal: true), ct);
            return;
        }
        if (ParseRef(action.Value) is not { } target)
            return;
        switch (action.ActionId)
        {
            case SendAction or RetryAction:
                await SendAsync(user, target, responseUrl, ct);
                break;
            case UpdateAction:
                await UpdateInTeamsAsync(user, target, responseUrl, ct);
                break;
            case DeleteAction:
                await DeleteInTeamsAsync(user, target, responseUrl, ct);
                break;
            case KeepDeletedAction:
                await KeepDeletedAsync(user, target, responseUrl, ct);
                break;
        }
    }

    private async Task SendAsync(string user, MessageRef r, string responseUrl, CancellationToken ct)
    {
        if (!IsSender(user))
        {
            await Respond(responseUrl, NotASender, ct);
            return;
        }
        var key = $"{r.Channel}/{r.Ts}";
        if (!_inFlight.TryAdd(key, 0))
            return; // 連點：第一次的還在送
        try
        {
            var bot = await tracker.BotUserIdAsync(ct) ?? throw new InvalidOperationException("Slack auth.test failed; can't tell which reactions are the bot's");

            // 按 Send 時才讀內容：確認前改過的錯字照改過的送。核對作者：只能送自己寫的
            HistoryMessage? message;
            try
            {
                message = await slack.GetMessageAsync(r.Channel, r.Ts, r.ThreadTs, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await RespondReadFailedAsync(responseUrl, r, ex, ct);
                return;
            }
            if (message is null || message.User != user)
            {
                await Respond(responseUrl, "That message is gone (deleted?), so nothing was sent.", ct);
                return;
            }
            if (tracker.IsSent(message, bot))
            {
                await Respond(responseUrl, "This message was already sent to Teams.", ct);
                return;
            }
            if (!SlackRichText.StartsWithMention(message.Text, bot))
            {
                await Respond(responseUrl, "The message no longer starts with the @mention, so it wasn't sent.", ct);
                return;
            }
            SendTarget target;
            try
            {
                target = await ResolveTargetAsync(r.Channel, r.ThreadTs, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Resolving the Teams chat for {Channel}/{Ts} failed", r.Channel, r.Ts);
                await RespondWithRetryAsync(responseUrl, r, "Not sent to Teams", $"Couldn't work out where to send it ({ex.Message}), so nothing was sent.", ct);
                return;
            }
            if (target.Error is { } error)
            {
                await Respond(responseUrl, error, ct);
                return;
            }
            var html = await ToHtmlAsync(message, bot, ct);
            if (SlackRichText.IsBlank(html))
            {
                await Respond(responseUrl, "There's nothing to send after the @mention.", ct);
                return;
            }

            var clientMessageId = SendTracker.ClientMessageId(r.Ts);
            if (SendTracker.IsSending(message, bot) || SendTracker.IsFailed(message, bot))
            {
                // 上次的嘗試沒有結論就先到對話裡找（這裡已經上了鎖，沒人在送）：停在 ⏳ 是送到一半 relay 重啟或 Slack 出錯；
                // ⚠️ 是當時查不到，但訊息可能之後才出現在對話裡。Teams 不去重，直接重送可能變兩則
                string? earlier;
                try
                {
                    earlier = await gateway.FindSentAsync(target.Chat!, clientMessageId, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // 查不了還是不知道：留著 Retry 之後再查（換一則重新 @bot 會是新的 ts，等於跳過這個檢查）
                    await RespondUnknownAsync(responseUrl, r, ex.Message, ct);
                    return;
                }
                if (earlier is not null)
                {
                    var recovered = tracker.Register(clientMessageId, r.Channel, r.Ts, inThread: r.ThreadTs is not null);
                    await ConfirmAndRespondAsync(recovered, earlier, target.Chat, responseUrl, r, ct, "This message was already sent to Teams.");
                    return;
                }
            }

            // 先登記再送：推回來可能比 API 回應早到
            var pending = tracker.Register(clientMessageId, r.Channel, r.Ts, inThread: r.ThreadTs is not null);
            try
            {
                await tracker.MarkSendingAsync(r.Channel, r.Ts, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // 還沒送：⏳ 沒按上的話 Retry 直接送，按上了也只是先查一次再送
                logger.LogWarning(ex, "Marking Slack message {Channel}/{Ts} as sending failed; nothing was sent", r.Channel, r.Ts);
                await RespondWithRetryAsync(responseUrl, r, "Not sent to Teams", $"Couldn't mark the message as sending ({ex.Message}), so nothing was sent.", ct);
                return;
            }
            await RespondSendingAsync(responseUrl, r, ct);

            var outcome = await gateway.SendAsync(target.Chat!, new SendMessageRequest(Html: html, ClientMessageId: clientMessageId, ReplyToId: target.ReplyToId), ct);
            string? verifyError = null;
            if (outcome.Status == SendStatus.Unknown)
            {
                // Teams 不會用 clientmessageid 去重，直接重送可能變兩則：先到對話裡找
                await Task.Delay(VerifyDelay, ct);
                try
                {
                    if (await gateway.FindSentAsync(target.Chat!, clientMessageId, ct) is { } found)
                        outcome = new SendOutcome(SendStatus.Sent, found, null);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    verifyError = ex.Message;
                }
            }

            if (outcome.Status == SendStatus.Sent || pending.Delivered)
            {
                await ConfirmAndRespondAsync(pending, outcome.MessageId, target.Chat, responseUrl, r, ct);
                return;
            }

            if (outcome.Status == SendStatus.Unknown && verifyError is not null)
            {
                // 結果不明：⏳ 與登記都留著（推回來時還認得），Retry 會先再查一次
                await RespondUnknownAsync(responseUrl, r, verifyError, ct);
                return;
            }

            bool failed;
            try
            {
                failed = await tracker.TryMarkFailedAsync(pending, clientMessageId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // ⚠️ 沒按上也一樣給 Retry：還停在 ⏳ 的話 Retry 會先查一次再送
                logger.LogWarning(ex, "Marking Slack message {Channel}/{Ts} as failed didn't work", r.Channel, r.Ts);
                failed = true;
            }
            if (!failed)
            {
                // 標失敗前已經推回來了：送到了。那邊換表情要是失敗過，這裡再試一次
                await ConfirmAndRespondAsync(pending, null, target.Chat, responseUrl, r, ct);
                return;
            }
            var why = outcome.Status == SendStatus.Rejected
                ? $"The gateway refused it ({outcome.Error})."
                : $"Teams didn't answer ({outcome.Error}), and the message isn't in the chat yet. Retry checks the chat again before sending.";
            logger.LogWarning("Sending Slack message {Channel}/{Ts} to Teams failed: {Why}", r.Channel, r.Ts, why);
            await RespondWithRetryAsync(responseUrl, r, "Not sent to Teams", why, ct);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Teams 收下了：換成已送出表情，確認那則收掉（給了 <paramref name="done"/> 就改成這句）。換表情失敗也要回話：
    /// 訊息已經在 Teams 上，不能給 Retry（Teams 不去重）。relay 沒重啟的話，推回來時會再試一次換表情；
    /// 一直沒換成的那則停在 ⏳，之後有人按 Send 也會先查到它已經送過。
    /// </summary>
    private async Task ConfirmAndRespondAsync(
        PendingSend send, string? teamsId, string? chat, string responseUrl, MessageRef r, CancellationToken ct, string? done = null)
    {
        try
        {
            await tracker.ConfirmAsync(send, teamsId, chat, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Sent Slack message {Channel}/{Ts} to Teams but couldn't update its status emoji", r.Channel, r.Ts);
            await Respond(responseUrl, $"Sent to Teams, but the status emoji couldn't be updated ({ex.Message}).", ct);
            return;
        }
        if (done is null)
            await slack.RespondAsync(responseUrl, new ResponseUrlRequest(DeleteOriginal: true), ct); // 已送出看表情
        else
            await Respond(responseUrl, done, ct);
    }

    /// <summary>
    /// 送出中的過場訊息，附一個 Check again：relay 送到一半重啟、或最後的結果沒回到這裡時，使用者還有路可走
    /// （那時沒人在送，按了會先到對話裡找再決定）。送出中按了會被鎖擋掉，什麼都不做。過場而已，回不了也照送。
    /// </summary>
    private async Task RespondSendingAsync(string responseUrl, MessageRef r, CancellationToken ct)
    {
        try
        {
            await slack.RespondAsync(responseUrl, new ResponseUrlRequest(
                Text: "Sending to Teams…",
                Blocks: [SlackBlock.Section("Sending to Teams… If this doesn't go away, press *Check again*: it looks for the message in Teams before sending anything."),
                         SlackBlock.Actions(SlackButton.Of("Check again", RetryAction, Ref(r.Channel, r.Ts, r.ThreadTs)))],
                ReplaceOriginal: true), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Showing the sending notice for {Channel}/{Ts} failed", r.Channel, r.Ts);
        }
    }

    /// <summary>讀 Slack 失敗（不是那則不見了）：沒送新的東西，給 Retry 再讀一次。</summary>
    private Task RespondReadFailedAsync(string responseUrl, MessageRef r, Exception ex, CancellationToken ct)
    {
        logger.LogWarning(ex, "Reading Slack message {Channel}/{Ts} for sending failed", r.Channel, r.Ts);
        return RespondWithRetryAsync(responseUrl, r, "Not sent to Teams", $"Reading the message from Slack failed ({ex.Message}), so nothing new was sent.", ct);
    }

    private Task RespondUnknownAsync(string responseUrl, MessageRef r, string error, CancellationToken ct)
    {
        var why = $"Teams may have received this message, but checking the chat failed ({error}). Retry will check again before sending.";
        logger.LogWarning("Sending Slack message {Channel}/{Ts} has an unknown outcome: {Why}", r.Channel, r.Ts, why);
        return RespondWithRetryAsync(responseUrl, r, "Send outcome unknown", why, ct);
    }

    private Task RespondWithRetryAsync(string responseUrl, MessageRef r, string title, string why, CancellationToken ct, string retryAction = RetryAction)
    {
        var value = Ref(r.Channel, r.Ts, r.ThreadTs);
        return slack.RespondAsync(responseUrl, new ResponseUrlRequest(
            Text: title,
            Blocks: [SlackBlock.Section($":warning: {title}. {Escape(why)}"),
                     SlackBlock.Actions(SlackButton.Of("Retry", retryAction, value, "primary"), SlackButton.Of("Dismiss", CancelAction, value))],
            ReplaceOriginal: true), ct);
    }

    /// <summary>
    /// 送去哪個對話。thread 裡：上層是自己的 bot 轉過來的 Teams 訊息，就送到它所在的對話、當成它的引用回覆；
    /// 上層是別的 app 轉的就不送；上層是從 Slack 發過去的那則，也是引用回覆它。其他（頂層、上層是頻道裡的討論）：頻道只對到一個對話才送。
    /// </summary>
    internal async Task<SendTarget> ResolveTargetAsync(string channel, string? threadTs, CancellationToken ct)
    {
        var routed = ChatsFor(channel);
        var parent = threadTs is null ? null : await slack.GetMessageAsync(channel, threadTs, null, ct);
        if (parent is { Metadata: { EventType: SlackClient.MetaEventType, EventPayload.TeamsId: { } teamsId } meta })
        {
            // 別的 app（例如本機開發用的 bot）轉的：它的 metadata 不採信，也不能退回頻道的規則亂猜，使用者在 thread 裡就是要回那一則
            if (!SlackClient.IsFrom(parent, (await bot.RequireAsync(ct)).BotId))
                return SendTarget.Fail("This thread's Teams message was relayed by another Slack app, so it can't be answered from here. Reply in Teams directly.");
            // 對話 id 夾在 metadata 裡；這個欄位上線前轉的沒有，頻道只對到一個對話時用它
            var chat = meta.EventPayload.Chat ?? (routed is [var only] ? only : null);
            return chat is null
                ? SendTarget.Fail("This Teams message was relayed before replies were supported, so its chat is unknown. Reply in Teams directly.")
                : new SendTarget(chat, teamsId, null);
        }
        // 上層是從 Slack 發過去的（bot 按了已送出表情）：它在 Teams 上就是一則訊息，回它就是引用它。
        // 頂層發話只會出現在只對到一個對話的頻道；Teams id 用它的 clientMessageId 到對話裡找，relay 重啟過也找得到
        if (parent is { BotId: null, Ts: { } parentTs } && routed is [var sentTo]
            && tracker.IsSent(parent, (await bot.RequireAsync(ct)).UserId))
        {
            return await gateway.FindSentAsync(sentTo, SendTracker.ClientMessageId(parentTs), ct) is { } sentId
                ? new SendTarget(sentTo, sentId, null)
                : SendTarget.Fail("Couldn't find this thread's message in Teams (deleted there?), so it can't be quoted. Reply in Teams directly.");
        }
        return routed switch
        {
            [var only] => new SendTarget(only, null, null),
            [] => SendTarget.Fail("This channel isn't linked to a Teams chat."),
            _ => SendTarget.Fail("Several Teams chats come into this channel, so it can't tell where to send. Reply in the thread of the Teams message instead."),
        };
    }

    /// <summary>
    /// 這個頻道收哪些 Teams 對話。有 Routes 時只看 Routes（DefaultChannel 用不到）；
    /// 沒有 Routes 而這是 DefaultChannel 時所有對話都進來，回 null（= 不知道送哪個）。
    /// </summary>
    private IReadOnlyList<string>? ChatsFor(string channel)
    {
        var slackOptions = options.Value.Slack;
        var routes = RelayWorker.BuildRoutes(slackOptions.Routes, NullLogger.Instance);
        if (routes.Count > 0)
            return routes.Where(r => r.Value == channel).Select(r => r.Key).ToList();
        return channel == slackOptions.DefaultChannel ? null : [];
    }

    private bool IsSender(string user) => options.Value.Slack.Senders.Contains(user);

    private async Task<string> ToHtmlAsync(HistoryMessage message, string bot, CancellationToken ct)
    {
        var (users, channels) = SlackRichText.References(message);
        foreach (var user in users.Where(u => !_userNames.ContainsKey(u)))
            if (await slack.GetUserNameAsync(user, ct) is { } name)
                _userNames[user] = name;
        foreach (var channel in channels.Where(c => !_channelNames.ContainsKey(c)))
            if (await slack.GetChannelNameAsync(channel, ct) is { } name)
                _channelNames[channel] = name;
        return SlackRichText.ToHtml(message, bot, _userNames.GetValueOrDefault, _channelNames.GetValueOrDefault);
    }

    /// <summary>確認訊息裡的對話名稱：從 gateway 的對話清單查（快取一陣子），1:1 沒有名稱。</summary>
    private async Task<string> ChatLabelAsync(string chat, CancellationToken ct)
    {
        if (_topics is not { } cached || DateTimeOffset.UtcNow - cached.At > TopicsTtl)
        {
            if (await chats.GetChatTopicsAsync(ct) is { } topics)
                _topics = cached = (topics, DateTimeOffset.UtcNow);
            else
                cached = _topics ?? (new Dictionary<string, string?>(), DateTimeOffset.MinValue); // 查不到就用舊的，沒有舊的只顯示 id
        }
        return cached.Topics.GetValueOrDefault(chat) ?? (cached.Topics.ContainsKey(chat) ? "a 1:1 chat" : chat);
    }

    private bool FirstTime(string? eventId)
    {
        if (eventId is null)
            return true;
        var cutoff = DateTimeOffset.UtcNow - SeenEventsTtl;
        foreach (var (id, at) in _seenEvents)
            if (at < cutoff)
                _seenEvents.TryRemove(id, out _);
        return _seenEvents.TryAdd(eventId, DateTimeOffset.UtcNow);
    }

    private Task Respond(string responseUrl, string text, CancellationToken ct) =>
        slack.RespondAsync(responseUrl, new ResponseUrlRequest(Text: text, ReplaceOriginal: true), ct);

    private const string NotASender = "You're not on the list of people who can send to Teams from Slack (Relay:Slack:Senders).";

    // 按鈕的 value：頻道|ts|thread_ts（頂層的 thread_ts 是空的）
    internal static string Ref(string channel, string ts, string? threadTs) => $"{channel}|{ts}|{threadTs}";

    internal static MessageRef? ParseRef(string? value) =>
        value?.Split('|') is [{ Length: > 0 } channel, { Length: > 0 } ts, var threadTs]
            ? new MessageRef(channel, ts, threadTs.Length > 0 ? threadTs : null)
            : null;

    private static string Escape(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    internal readonly record struct MessageRef(string Channel, string Ts, string? ThreadTs);

    /// <summary>送去的對話；<c>ReplyToId</c> 有值 = 送成那則的引用回覆。<c>Error</c> 有值 = 送不了，原因給人看。</summary>
    internal readonly record struct SendTarget(string? Chat, string? ReplyToId, string? Error)
    {
        public static SendTarget Fail(string error) => new(null, null, error);
    }
}
