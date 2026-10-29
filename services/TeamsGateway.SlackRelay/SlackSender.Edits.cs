using System.Net;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// 送到 Teams 之後在 Slack 上改、刪（ADR 0013）。不自動同步：Teams 那邊是對外的，順手改個錯字不該直接改過去。
/// 回只有本人看得到的確認，按了才改 / 刪 Teams 上那則。改、刪重送都安全，失敗給 Retry。
/// Teams 推回來時頂層發的那則不再貼「Edited / Deleted in Teams」：轉送那邊看 Slack 上已經是這個樣子就不貼（<see cref="RelayWorker"/>）。
/// </summary>
public sealed partial class SlackSender
{
    internal const string UpdateAction = "update";
    internal const string DeleteAction = "delete";

    private const string MessageChanged = "message_changed";
    private const string MessageDeleted = "message_deleted";
    private const string Tombstone = "tombstone";

    /// <summary>
    /// 頻道裡的訊息被改 / 刪（<c>message.channels</c>）。只理會轉送的頻道、白名單裡的人、已經送到 Teams 的那幾則。
    /// 有回覆的頂層訊息被刪時，Slack 送的是 <c>message_changed</c>、新版是 <c>tombstone</c>，一樣當成刪除；
    /// 之後那則 thread 有動靜時 Slack 還會再送（舊版也是 tombstone），不再問。
    /// </summary>
    internal async Task OnMessageChangedAsync(SlackEvent evt, CancellationToken ct)
    {
        if (evt.Channel is not { } channel || ChatsFor(channel) is [] || evt.PreviousMessage?.Subtype == Tombstone
            || await tracker.BotUserIdAsync(ct) is not { } bot)
            return;
        if (evt.Subtype == MessageDeleted || evt.Message?.Subtype == Tombstone)
            await OnDeletedAsync(channel, evt.PreviousMessage, evt.DeletedTs ?? evt.Message?.Ts, bot, ct);
        else if (evt.Message is { Edited: not null } edited && edited.Text != evt.PreviousMessage?.Text)
            await OnEditedAsync(channel, edited, evt.PreviousMessage, bot, ct); // 沒有 edited 的是 Slack 自己補的（連結預覽之類），內容沒變
    }

    /// <summary>還沒按 Send 的改了就改了（按 Send 時本來就讀最新的）；已經送出去的才問要不要也改 Teams 上那則。</summary>
    private async Task OnEditedAsync(string channel, HistoryMessage edited, HistoryMessage? before, string bot, CancellationToken ct)
    {
        if (edited is not { User: { } user, Ts: { } ts } || !IsSender(user))
            return;
        var threadTs = (edited.ThreadTs ?? before?.ThreadTs) is { } t && t != ts ? t : null;
        // 事件裡的那份不保證帶表情：重讀一次看送過了沒
        HistoryMessage? current;
        try
        {
            current = await slack.GetMessageAsync(channel, ts, threadTs, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Reading edited Slack message {Channel}/{Ts} failed; not offering to update Teams", channel, ts);
            return;
        }
        if (current is null || !tracker.IsSent(current, bot))
            return;
        var value = Ref(channel, ts, threadTs);
        await slack.PostEphemeralAsync(channel, user, "Already sent to Teams. Update it there too?",
            [SlackBlock.Section($"*Already sent to Teams.* Update it there too?\n{SlackRichText.Preview(current.Text, bot)}"),
             SlackBlock.Actions(SlackButton.Of("Update", UpdateAction, value, "primary"), SlackButton.Of("Keep", CancelAction, value))],
            threadTs, ct);
    }

    /// <summary>
    /// 刪掉的讀不到了，只能看事件裡刪之前那份：作者、在不在 thread、有沒有已送出表情。那份沒附表情時，
    /// 開頭不是 @bot 的一定沒送過，是的話到 Teams 查一次，免得問一則根本沒送出去的。
    /// </summary>
    private async Task OnDeletedAsync(string channel, HistoryMessage? before, string? ts, string bot, CancellationToken ct)
    {
        if (before is not { User: { } user } || ts is null || !IsSender(user))
            return;
        var threadTs = before.ThreadTs is { } t && t != ts ? t : null;
        if (!tracker.IsSent(before, bot)
            && (before.Reactions is not null || !SlackRichText.StartsWithMention(before.Text, bot)
                || await FindInTeamsQuietlyAsync(new MessageRef(channel, ts, threadTs), ct) is null))
            return;
        var value = Ref(channel, ts, threadTs);
        await slack.PostEphemeralAsync(channel, user, "Deleted here, but it's still in Teams. Delete it there too?",
            [SlackBlock.Section($"*Deleted here, but it's still in Teams.* Delete it there too?\n{SlackRichText.Preview(before.Text, bot)}"),
             SlackBlock.Actions(SlackButton.Of("Delete", DeleteAction, value, "danger"), SlackButton.Of("Keep", CancelAction, value))],
            threadTs, ct);
    }

    private async Task UpdateInTeamsAsync(string user, MessageRef r, string responseUrl, CancellationToken ct) =>
        await WithMessageLockAsync(user, r, responseUrl, ct, async () =>
        {
            var bot = await tracker.BotUserIdAsync(ct) ?? throw new InvalidOperationException("Slack auth.test failed; can't tell which reactions are the bot's");
            HistoryMessage? message;
            SendTarget target;
            MessageDto? sent;
            try
            {
                // 按 Update 時才讀內容：確認前又改了就送最後那版
                message = await slack.GetMessageAsync(r.Channel, r.Ts, r.ThreadTs, ct);
                if (message is null || message.User != user)
                {
                    await Respond(responseUrl, "That message is gone (deleted?), so Teams wasn't updated.", ct);
                    return;
                }
                if (!tracker.IsSent(message, bot))
                {
                    await Respond(responseUrl, "That message wasn't sent to Teams, so there's nothing to update.", ct);
                    return;
                }
                (target, sent) = await FindSentFromSlackAsync(r, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Looking up Slack message {Channel}/{Ts} for updating Teams failed", r.Channel, r.Ts);
                await RespondWithRetryAsync(responseUrl, r, "Not updated in Teams", $"Looking the message up failed ({ex.Message}), so nothing was changed.", ct, UpdateAction);
                return;
            }
            if (target.Error is { } error)
            {
                await Respond(responseUrl, error, ct);
                return;
            }
            if (sent is null or { Deleted: true })
            {
                await Respond(responseUrl, "It isn't in Teams anymore (deleted there?), so there's nothing to update.", ct);
                return;
            }
            var html = await tracker.ToHtmlAsync(message, bot, ct);
            if (SlackRichText.IsBlank(html))
            {
                await Respond(responseUrl, "There's nothing left after the @mention. To take it off Teams, delete the message here instead.", ct);
                return;
            }

            // Teams 的編輯整則換掉，原本的引用要再帶，不然變成普通訊息。引用看 Teams 上那則現在引用誰：
            // Slack 上的 thread 上層可能已經刪了，從那邊推不回來
            var replyToId = TeamsText.Quotes(sent.Content).Select(q => q.Id).FirstOrDefault();
            var outcome = await gateway.EditAsync(target.Chat!, sent.Id, new EditMessageRequest(Html: html, ReplyToId: replyToId), ct);
            if (outcome.Ok)
                await Respond(responseUrl, ":white_check_mark: Updated in Teams.", ct);
            else if (outcome.Gone)
                await Respond(responseUrl, outcome.Status == HttpStatusCode.Conflict
                    ? "It was deleted in Teams, so it can't be updated."
                    : "It isn't in Teams anymore, so there's nothing to update.", ct);
            else
                await RespondChangeFailedAsync(responseUrl, r, "Not updated in Teams", outcome, UpdateAction, ct);
        });

    private async Task DeleteInTeamsAsync(string user, MessageRef r, string responseUrl, CancellationToken ct) =>
        await WithMessageLockAsync(user, r, responseUrl, ct, async () =>
        {
            (SendTarget Target, MessageDto? Sent) found;
            try
            {
                found = await FindSentFromSlackAsync(r, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Looking up Slack message {Channel}/{Ts} in Teams failed", r.Channel, r.Ts);
                await RespondWithRetryAsync(responseUrl, r, "Not deleted in Teams", $"Looking the message up failed ({ex.Message}), so nothing was changed.", ct, DeleteAction);
                return;
            }
            var (target, sent) = found;
            if (target.Error is { } error)
            {
                await Respond(responseUrl, error, ct);
                return;
            }
            if (sent is null)
            {
                await Respond(responseUrl, "It isn't in Teams anymore, so there's nothing to delete.", ct);
                return;
            }
            if (sent.Deleted)
            {
                await Respond(responseUrl, ":white_check_mark: It was already deleted in Teams.", ct);
                return;
            }
            var outcome = await gateway.DeleteAsync(target.Chat!, sent.Id, ct);
            if (outcome.Ok || outcome.Gone)
                await Respond(responseUrl, ":white_check_mark: Deleted in Teams.", ct);
            else
                await RespondChangeFailedAsync(responseUrl, r, "Not deleted in Teams", outcome, DeleteAction, ct);
        });

    /// <summary>白名單、同一則同時只處理一個（跟發話共用，送到一半不能改 / 刪）。</summary>
    private async Task WithMessageLockAsync(string user, MessageRef r, string responseUrl, CancellationToken ct, Func<Task> change)
    {
        if (!IsSender(user))
        {
            await Respond(responseUrl, NotASender, ct);
            return;
        }
        var key = $"{r.Channel}/{r.Ts}";
        if (!_inFlight.TryAdd(key, 0))
            return;
        try
        {
            await change();
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// 從這則 Slack 訊息送出去的那則：送去的對話（跟發話時同一套規則），與 Teams 上的那則（用 clientMessageId 找，重啟過也找得到；
    /// 沒有是 null）。查不了丟例外。
    /// </summary>
    private async Task<(SendTarget Target, MessageDto? Sent)> FindSentFromSlackAsync(MessageRef r, CancellationToken ct)
    {
        var target = await ResolveTargetAsync(r.Channel, r.ThreadTs, ct);
        return target.Error is not null
            ? (target, null)
            : (target, await gateway.FindSentMessageAsync(target.Chat!, SendTracker.ClientMessageId(r.Ts), ct));
    }

    private async Task<string?> FindInTeamsQuietlyAsync(MessageRef r, CancellationToken ct)
    {
        try
        {
            var (target, sent) = await FindSentFromSlackAsync(r, ct);
            return target.Error is null && sent is { Deleted: false } ? sent.Id : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Looking up deleted Slack message {Channel}/{Ts} in Teams failed", r.Channel, r.Ts);
            return null;
        }
    }

    /// <summary>改、刪重送都安全：連不上、5xx 給 Retry；請求本身被拒（4xx，例如 gateway 還沒有這個端點）重試也沒用，只說明。</summary>
    private Task RespondChangeFailedAsync(string responseUrl, MessageRef r, string title, ChangeOutcome outcome, string retryAction, CancellationToken ct)
    {
        logger.LogWarning("{Title}: Slack message {Channel}/{Ts}: {Error}", title, r.Channel, r.Ts, outcome.Error);
        return outcome.Retryable
            ? RespondWithRetryAsync(responseUrl, r, title, $"The gateway didn't take it ({outcome.Error}). Retrying is safe.", ct, retryAction)
            : Respond(responseUrl, $":warning: {title}. The gateway refused it ({outcome.Error}).", ct);
    }
}
