using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// Slack Web API：發訊息 / 原地更新 / thread 回覆 / 取 permalink / 加入頻道 / 讀歷史 / 上傳圖片。
/// 轉過去的訊息會夾一個 metadata（Teams 訊息 id），讓重啟時能從 Slack 歷史重建對應；表情留言夾事件序號，重送時認得出貼過了。
/// 有圖的訊息改用 blocks：文字 section + 每張圖一個 image block（引用先上傳好的 Slack 檔案）。
/// </summary>
public sealed class SlackClient(HttpClient http, ILogger<SlackClient> logger)
{
    internal const string MetaEventType = "teams_relay";
    private const string ReactionEventType = "teams_reaction";
    private const string EditNoteEventType = "teams_edit_note";
    private const int SectionMaxChars = 3000;

    // 剛上傳的檔 Slack 可能還沒處理完，這時引用它的 image block 會回 invalid_blocks
    private static readonly TimeSpan[] FileNotReadyDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    // version：目前顯示的是 Teams 哪一版（毫秒時間戳，原文 = 訊息 id、編輯後 = 編輯時間），重啟回補後留證才標得出時間
    private static SlackMetadata? Meta(string? teamsId, long? version, string? chat) =>
        teamsId is null ? null : new SlackMetadata(MetaEventType, new SlackRelayPayload(teamsId, version, Chat: chat));

    /// <summary>發訊息（<paramref name="teamsId"/> / <paramref name="version"/> / <paramref name="chat"/> 夾進 metadata、<paramref name="images"/> 是已上傳的 Slack file id）；回傳貼成的 ts，失敗 null。</summary>
    public async Task<string?> PostMessageAsync(string channel, string text, string? teamsId, long? version, IReadOnlyList<string> images, CancellationToken ct, string? chat = null)
    {
        var result = await SendAsync("chat.postMessage", channel, text, images, clearBlocks: false,
            (t, blocks) => new SlackMessageRequest(channel, t, blocks, Metadata: Meta(teamsId, version, chat)), ct);
        return result?.Ts;
    }

    /// <summary>原地更新一則訊息（編輯用；Slack 自帶 "edited"）。metadata 要一起帶，不然更新後會掉。回傳有沒有更新成。</summary>
    public async Task<bool> UpdateMessageAsync(string channel, string ts, string text, string teamsId, long? version, IReadOnlyList<string> images, CancellationToken ct, string? chat = null) =>
        await SendAsync("chat.update", channel, text, images, clearBlocks: true,
            (t, blocks) => new SlackMessageRequest(channel, t, blocks, Ts: ts, Metadata: Meta(teamsId, version, chat)), ct) is not null;

    /// <summary>在某則訊息的 thread 底下回一則（舊版留證用）；回傳有沒有貼成。</summary>
    public async Task<bool> PostReplyAsync(string channel, string threadTs, string text, IReadOnlyList<string> images, CancellationToken ct) =>
        await SendAsync("chat.postMessage", channel, text, images, clearBlocks: false,
            (t, blocks) => new SlackMessageRequest(channel, t, blocks, ThreadTs: threadTs), ct) is not null;

    /// <summary>在某則訊息的 thread 底下留表情留言，夾著 gateway 的事件序號 <paramref name="seq"/>（見 <see cref="ReadReactionSeqsAsync"/>）；回傳有沒有貼成。</summary>
    public async Task<bool> PostReactionNoteAsync(string channel, string threadTs, string text, long? seq, CancellationToken ct) =>
        await SendAsync("chat.postMessage", channel, text, [], clearBlocks: false,
            (t, blocks) => new SlackMessageRequest(channel, t, blocks, ThreadTs: threadTs,
                Metadata: seq is null ? null : new SlackMetadata(ReactionEventType, new SlackRelayPayload(null, null, seq))), ct) is not null;

    /// <summary>
    /// 上傳一張圖但不分享到任何頻道（留 private），回傳 file id 給 image block 的 <c>slack_file</c> 引用；失敗 null。
    /// 三步：<c>files.getUploadURLExternal</c> → POST 檔案內容 → <c>files.completeUploadExternal</c>。需要 <c>files:write</c>。
    /// </summary>
    public async Task<string?> UploadFileAsync(byte[] bytes, string filename, CancellationToken ct)
    {
        using var ticketResp = await http.PostAsync("files.getUploadURLExternal", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["filename"] = filename,
            ["length"] = bytes.Length.ToString(CultureInfo.InvariantCulture),
        }), ct);
        var ticket = await ticketResp.Content.ReadFromJsonAsync(SlackJsonContext.Default.UploadTicketResponse, ct);
        if (ticket is not { Ok: true, UploadUrl: { } uploadUrl, FileId: { } fileId })
        {
            logger.LogWarning("Slack files.getUploadURLExternal 失敗：{Error}{Hint}",
                ticket?.Error ?? ((int)ticketResp.StatusCode).ToString(CultureInfo.InvariantCulture),
                ticket?.Error == "missing_scope" ? "（Slack app 要加 files:write 並重新安裝）" : "");
            return null;
        }

        using (var uploadResp = await http.PostAsync(uploadUrl, new ByteArrayContent(bytes), ct))
        {
            if (!uploadResp.IsSuccessStatusCode)
            {
                logger.LogWarning("上傳圖片內容到 Slack 失敗 HTTP {Status}", (int)uploadResp.StatusCode);
                return null;
            }
        }

        var files = JsonSerializer.Serialize([new UploadedFile(fileId, filename)], SlackJsonContext.Default.UploadedFileArray);
        using var doneResp = await http.PostAsync("files.completeUploadExternal",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["files"] = files }), ct);
        var done = await doneResp.Content.ReadFromJsonAsync(SlackJsonContext.Default.PostResponse, ct);
        if (done is { Ok: true })
            return fileId;
        logger.LogWarning("Slack files.completeUploadExternal 失敗：{Error}", done?.Error ?? ((int)doneResp.StatusCode).ToString(CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>某則訊息的 permalink（貼進訊息裡 Slack 會 unfurl 成原生引用）；取不到回 null。</summary>
    public async Task<string?> GetPermalinkAsync(string channel, string ts, CancellationToken ct)
    {
        var url = $"chat.getPermalink?channel={Uri.EscapeDataString(channel)}&message_ts={Uri.EscapeDataString(ts)}";
        using var resp = await http.GetAsync(url, ct);
        var result = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.PermalinkResponse, ct);
        return result is { Ok: true } ? result.Permalink : null;
    }

    /// <summary>
    /// bot 在某則訊息按 / 收回一個原生表情（需要 <c>reactions:write</c>）。已經按過 / 本來就沒按當成成功；
    /// 回傳 false = Slack 不認得這個表情名稱（<c>invalid_name</c>）。其他失敗都丟例外（限流、Slack 自己出錯是
    /// <see cref="SlackTransientException"/>）：發話的狀態表情是防重送的依據，不能默默失敗。
    /// 只是顯示的呼叫端自己吞掉：鏡像 Teams 表情、自訂的已送出顯示表情。
    /// </summary>
    public async Task<bool> ReactAsync(string channel, string ts, string name, bool add, CancellationToken ct)
    {
        var method = add ? "reactions.add" : "reactions.remove";
        using var resp = await http.PostAsJsonAsync(method, new ReactionRequest(channel, ts, name), SlackJsonContext.Default.ReactionRequest, ct);
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new SlackTransientException(method, "ratelimited", resp.Headers.RetryAfter?.Delta);
        var result = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.PostResponse, ct);
        if (result is { Error: "invalid_name" })
            return false;
        if (result is { Ok: true } or { Error: "already_reacted" or "no_reaction" })
            return true;
        var error = result?.Error ?? ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture);
        logger.LogWarning("Slack {Method} failed (channel={Channel}, name={Name}): {Error}{Hint}", method, channel, name,
            error, result?.Error == "missing_scope" ? " (add reactions:write to the Slack app and reinstall it)" : "");
        if (TransientErrors.Contains(error))
            throw new SlackTransientException(method, error, resp.Headers.RetryAfter?.Delta);
        throw new InvalidOperationException($"Slack {method} failed: {error}");
    }

    // ---- Slack → Teams 發話（ADR 0012）用到的 ----

    /// <summary>bot 自己是誰（<c>auth.test</c>，見 <see cref="SlackBotIdentity"/>）。查不到回 null。</summary>
    public async Task<BotIdentity?> GetBotIdentityAsync(CancellationToken ct)
    {
        using var resp = await http.PostAsync("auth.test", null, ct);
        var result = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.AuthTestResponse, ct);
        if (result is { Ok: true, UserId: { } id })
            return new BotIdentity(id, result.BotId);
        logger.LogWarning("Slack auth.test failed: {Error}", result?.Error ?? ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>只有 <paramref name="user"/> 看得到的訊息（<c>chat.postEphemeral</c>）；在 thread 裡要帶 <paramref name="threadTs"/>。</summary>
    internal async Task<bool> PostEphemeralAsync(string channel, string user, string text, IReadOnlyList<SlackBlock>? blocks, string? threadTs, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync("chat.postEphemeral", new EphemeralRequest(channel, user, text, blocks, threadTs),
            SlackJsonContext.Default.EphemeralRequest, ct);
        var result = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.PostResponse, ct);
        if (result is { Ok: true })
            return true;
        logger.LogWarning("Slack chat.postEphemeral failed (channel={Channel}): {Error}", channel,
            result?.Error ?? ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
        return false;
    }

    /// <summary>
    /// 經按鈕事件的 <c>response_url</c> 改掉或刪掉那則 ephemeral（ephemeral 只能這樣改）。網址來自 Socket Mode 的事件，
    /// 還是只接受 Slack 自己的主機：bot token 在預設 header 裡，不能送去別的地方。
    /// </summary>
    internal async Task RespondAsync(string responseUrl, ResponseUrlRequest body, CancellationToken ct)
    {
        if (!IsSlackUrl(responseUrl))
        {
            logger.LogWarning("Ignoring a response_url outside slack.com: {Url}", responseUrl);
            return;
        }
        using var resp = await http.PostAsJsonAsync(responseUrl, body, SlackJsonContext.Default.ResponseUrlRequest, ct);
        if (!resp.IsSuccessStatusCode)
            logger.LogWarning("Slack response_url returned HTTP {Status}", (int)resp.StatusCode);
    }

    internal static bool IsSlackUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "slack.com" || uri.Host.EndsWith(".slack.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 讀頻道裡的一則訊息，thread 裡的回覆要給 <paramref name="threadTs"/>（history 讀不到 thread 裡的）。
    /// 一定核對 ts：那則被刪掉時 Slack 會回範圍內的別則（replies 也一定先回上層那則），不核對就會讀到別人的話。
    /// 那則不在（或 thread 不在）回 null；其他讀不到的情況丟例外，不能讓「Slack 一時出錯」看起來像「那則被刪了」。
    /// </summary>
    internal async Task<HistoryMessage?> GetMessageAsync(string channel, string ts, string? threadTs, CancellationToken ct)
    {
        var range = $"oldest={Uri.EscapeDataString(ts)}&latest={Uri.EscapeDataString(ts)}&inclusive=true";
        var (method, query) = threadTs is null || threadTs == ts
            ? ("conversations.history", range)
            : ("conversations.replies", $"ts={Uri.EscapeDataString(threadTs)}&{range}");
        await foreach (var m in ReadPagesAsync(method, channel, query, ct, pages: 1, throwOnError: true))
            if (m.Ts == ts)
                return m;
        return null;
    }

    /// <summary>一則訊息連同上面的表情（<c>reactions.get</c>，需要 <c>reactions:read</c>）；查不到回 null。</summary>
    internal async Task<HistoryMessage?> GetReactionsAsync(string channel, string ts, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"reactions.get?channel={Uri.EscapeDataString(channel)}&timestamp={Uri.EscapeDataString(ts)}&full=true", ct);
        var result = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.ReactionsResponse, ct);
        if (result is { Ok: true, Message: { } message })
            return message;
        if (result?.Error is not "message_not_found")
            logger.LogWarning("Slack reactions.get failed (channel={Channel}): {Error}{Hint}", channel,
                result?.Error ?? ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture),
                result?.Error == "missing_scope" ? " (add reactions:read to the Slack app and reinstall it)" : "");
        return null;
    }

    /// <summary>Slack 使用者的顯示名稱（<c>users.info</c>，需要 <c>users:read</c>）；查不到回 null。</summary>
    public async Task<string?> GetUserNameAsync(string user, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"users.info?user={Uri.EscapeDataString(user)}", ct);
        var result = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.UserInfoResponse, ct);
        if (result is { Ok: true, User: { } u })
            return new[] { u.Profile?.DisplayName, u.Profile?.RealName, u.RealName, u.Name }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        logger.LogWarning("Slack users.info failed (user={User}): {Error}", user,
            result?.Error ?? ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>Socket Mode 的連線網址（<c>apps.connections.open</c>，要用 app-level token，不是 bot token）。失敗回 null。</summary>
    public async Task<Uri?> OpenSocketAsync(string appToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "apps.connections.open");
        req.Headers.Authorization = new("Bearer", appToken);
        using var resp = await http.SendAsync(req, ct);
        var result = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.ConnectionsOpenResponse, ct);
        if (result is { Ok: true, Url: { } url } && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri;
        logger.LogWarning("Slack apps.connections.open failed: {Error}{Hint}",
            result?.Error ?? ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture),
            result?.Error is "invalid_auth" or "not_allowed_token_type" ? " (Relay:Slack:AppToken must be an app-level xapp- token with connections:write)" : "");
        return null;
    }

    /// <summary>頻道名稱（不含 <c>#</c>），啟動時印路由用；需要 <c>channels:read</c>（private 頻道 <c>groups:read</c>），查不到回 null。</summary>
    public async Task<string?> GetChannelNameAsync(string channel, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"conversations.info?channel={Uri.EscapeDataString(channel)}", ct);
        var result = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.ChannelInfoResponse, ct);
        if (result is { Ok: true, Channel.Name: { } name })
            return name;
        logger.LogWarning("Slack conversations.info 失敗（channel={Channel}）：{Error}{Hint}", channel,
            result?.Error ?? ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture),
            result?.Error == "missing_scope" ? "（Slack app 要加 channels:read 並重新安裝；private 頻道要 groups:read）" : "");
        return null;
    }

    /// <summary>bot 加入頻道（才讀得到歷史；public 頻道用 <c>channels:join</c>）。已在裡面也回 ok。</summary>
    public async Task JoinChannelAsync(string channel, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync("conversations.join", new JoinRequest(channel), SlackJsonContext.Default.JoinRequest, ct);
        var result = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.PostResponse, ct);
        if (result is not { Ok: true })
            logger.LogWarning("Slack conversations.join 失敗（channel={Channel}）：{Error}", channel, result?.Error ?? ((int)resp.StatusCode).ToString());
    }

    /// <summary>
    /// 這則是不是 <paramref name="botId"/> 貼的。下面讀 metadata 的都只認自己的 bot：讀歷史帶了 <c>include_all_metadata</c>，
    /// 同一個頻道裡別的 app（例如本機開發用的另一個 bot）夾的同格式 metadata 也會讀到。<paramref name="botId"/> 是 null（查不到自己是誰）就不分。
    /// </summary>
    internal static bool IsFrom(HistoryMessage m, string? botId) => botId is null || m.BotId == botId;

    /// <summary>讀頻道歷史，撈出我們轉過（帶 metadata）的訊息：(Teams id, ts, 當前文字, 版本)。往回翻到 <paramref name="oldestUnixSeconds"/>。</summary>
    public async Task<IReadOnlyList<RelayedMessage>> ReadRelayedAsync(string channel, long oldestUnixSeconds, string? botId, CancellationToken ct)
    {
        var found = new List<RelayedMessage>();
        await foreach (var m in ReadPagesAsync("conversations.history", channel, $"oldest={oldestUnixSeconds}", ct))
            if (AsRelayed(m, botId) is { } relayed)
                found.Add(relayed);
        return found;
    }

    /// <summary>頻道裡最新一則我們轉過的訊息，不限時間往回找（最多 20 頁）；一則都沒有回 null。</summary>
    public async Task<RelayedMessage?> ReadNewestRelayedAsync(string channel, string? botId, CancellationToken ct)
    {
        await foreach (var m in ReadPagesAsync("conversations.history", channel, "", ct))
            if (AsRelayed(m, botId) is { } relayed)
                return relayed;
        return null;
    }

    private static RelayedMessage? AsRelayed(HistoryMessage m, string? botId) =>
        IsFrom(m, botId) && m.Metadata?.EventType == MetaEventType && m.Metadata.EventPayload?.TeamsId is { } tid && m.Ts is { } ts
            ? new RelayedMessage(tid, ts, m.Text ?? "", m.Metadata.EventPayload.Version, m.Metadata.EventPayload.Chat)
            : null;

    /// <summary>某則訊息 thread 裡已經貼過的表情留言的事件序號（<see cref="PostReactionNoteAsync"/> 夾的 metadata）；讀不到就是空的。</summary>
    public async Task<IReadOnlyCollection<long>> ReadReactionSeqsAsync(string channel, string threadTs, string? botId, CancellationToken ct)
    {
        var seqs = new HashSet<long>();
        await foreach (var m in ReadPagesAsync("conversations.replies", channel, $"ts={Uri.EscapeDataString(threadTs)}", ct))
            if (IsFrom(m, botId) && m.Metadata?.EventType == ReactionEventType && m.Metadata.EventPayload?.Seq is { } seq)
                seqs.Add(seq);
        return seqs;
    }

    /// <summary>
    /// 在人從 Slack 發的那則（ADR 0012）的 thread 貼 Teams 上的新版（「Edited / Deleted in Teams」），夾著 Teams id 與版本：
    /// 重啟後 gateway 重送同一個編輯時，<see cref="ReadEditNoteVersionAsync"/> 認得出貼過了。回傳有沒有貼成。
    /// </summary>
    public async Task<bool> PostEditNoteAsync(string channel, string threadTs, string text, IReadOnlyList<string> images, string teamsId, long? version, CancellationToken ct) =>
        await SendAsync("chat.postMessage", channel, text, images, clearBlocks: false,
            (t, blocks) => new SlackMessageRequest(channel, t, blocks, ThreadTs: threadTs,
                Metadata: new SlackMetadata(EditNoteEventType, new SlackRelayPayload(teamsId, version))), ct) is not null;

    /// <summary>那則 thread 裡已經貼過的 Teams 新版裡最新的版本（<see cref="PostEditNoteAsync"/> 夾的）；沒貼過回 null。</summary>
    public async Task<long?> ReadEditNoteVersionAsync(string channel, string threadTs, string teamsId, string? botId, CancellationToken ct)
    {
        long? newest = null;
        await foreach (var m in ReadPagesAsync("conversations.replies", channel, $"ts={Uri.EscapeDataString(threadTs)}", ct))
            if (IsFrom(m, botId) && m.Metadata is { EventType: EditNoteEventType, EventPayload: { Version: { } version } payload } && payload.TeamsId == teamsId
                && version > (newest ?? long.MinValue))
                newest = version;
        return newest;
    }

    /// <summary><c>conversations.history</c> / <c>conversations.replies</c> 共用的翻頁（帶 metadata）。失敗記 warning，停在已讀到的地方。</summary>
    private async IAsyncEnumerable<HistoryMessage> ReadPagesAsync(
        string method, string channel, string query, [EnumeratorCancellation] CancellationToken ct, int pages = 20, bool throwOnError = false)
    {
        string? cursor = null;
        for (var page = 0; page < pages; page++) // 上限保護
        {
            var url = $"{method}?channel={Uri.EscapeDataString(channel)}{(query.Length > 0 ? "&" + query : "")}&limit=200&include_all_metadata=true"
                      + (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var resp = await http.GetAsync(url, ct);
            var body = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.HistoryResponse, ct);
            if (body is not { Ok: true })
            {
                var error = body?.Error ?? ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture);
                logger.LogWarning("Slack {Method} failed (channel={Channel}): {Error}", method, channel, error);
                // throwOnError：呼叫端要分得出「沒有」與「讀不到」
                if (throwOnError && error is not ("thread_not_found" or "message_not_found"))
                    throw TransientErrors.Contains(error)
                        ? new SlackTransientException(method, error, resp.Headers.RetryAfter?.Delta)
                        : new InvalidOperationException($"Slack {method} failed: {error}");
                yield break;
            }
            foreach (var m in body.Messages ?? [])
                yield return m;
            cursor = body.ResponseMetadata?.NextCursor;
            if (string.IsNullOrEmpty(cursor))
                yield break;
        }
    }

    /// <summary>文字切成 mrkdwn section（每塊上限 3000 字）+ 每張圖一個引用 Slack 檔案的 image block。</summary>
    internal static IReadOnlyList<SlackBlock> ImageBlocks(string text, IReadOnlyList<string> fileIds)
    {
        var blocks = new List<SlackBlock>();
        for (var i = 0; i < text.Length; i += SectionMaxChars)
            blocks.Add(SlackBlock.Section(text.Substring(i, Math.Min(SectionMaxChars, text.Length - i))));
        blocks.AddRange(fileIds.Select(SlackBlock.Image));
        return blocks;
    }

    // Slack 那邊暫時性的錯誤（https://docs.slack.dev/apis/web-api/#errors）：重試會好，不能當成這則送不出去
    private static readonly HashSet<string> TransientErrors = ["ratelimited", "internal_error", "fatal_error", "service_unavailable", "request_timeout"];

    /// <summary>
    /// 送一則訊息：有圖就帶 blocks（文字 section + image block），沒圖只帶 text。
    /// 圖剛上傳完 Slack 可能回 invalid_blocks，等一下重試；一直不行就拿掉圖、文字補註記改送純文字，至少內容送得到。
    /// <paramref name="clearBlocks"/>：chat.update 沒帶 blocks 時 Slack 會保留舊的（舊圖留著），所以沒圖要明確送空陣列。
    /// 限流、Slack 自己出錯丟 <see cref="SlackTransientException"/> 讓呼叫端重試；其他錯誤（頻道不存在、沒權限…）重試也沒用，記 error 回 null。
    /// </summary>
    private async Task<PostResponse?> SendAsync(
        string method, string channel, string text, IReadOnlyList<string> images, bool clearBlocks,
        Func<string, IReadOnlyList<SlackBlock>?, SlackMessageRequest> body, CancellationToken ct)
    {
        var withImages = images.Count > 0;
        for (var attempt = 0; ; attempt++)
        {
            IReadOnlyList<SlackBlock>? blocks = withImages ? ImageBlocks(text, images) : clearBlocks ? [] : null;
            using var resp = await http.PostAsJsonAsync(method, body(text, blocks), SlackJsonContext.Default.SlackMessageRequest, ct);
            // 429 的 body 不一定是 JSON，先看狀態碼
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new SlackTransientException(method, "ratelimited", resp.Headers.RetryAfter?.Delta);
            var result = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.PostResponse, ct);
            if (result is { Ok: false, Error: { } error } && TransientErrors.Contains(error))
                throw new SlackTransientException(method, error, resp.Headers.RetryAfter?.Delta);
            if (withImages && result is { Ok: false, Error: "invalid_blocks" })
            {
                if (attempt < FileNotReadyDelays.Length)
                {
                    await Task.Delay(FileNotReadyDelays[attempt], ct);
                    continue;
                }
                logger.LogWarning("Slack {Method} 帶圖一直 invalid_blocks（channel={Channel}），改送純文字", method, channel);
                withImages = false;
                text += $"\n_({images.Count} image{(images.Count == 1 ? "" : "s")} could not be displayed)_";
                continue;
            }
            if (result is { Ok: true })
                return result;
            logger.LogError("Slack {Method} 失敗（channel={Channel}）：{Error}", method, channel, result?.Error ?? ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
            return null;
        }
    }

    public readonly record struct RelayedMessage(string TeamsId, string Ts, string Text, long? Version, string? Chat = null);
}

/// <summary>Slack 回了暫時性的錯誤（限流、Slack 自己出錯），重試會好。<see cref="RetryAfter"/> 是 Slack 要我們等多久（有給的話）。</summary>
public sealed class SlackTransientException(string method, string error, TimeSpan? retryAfter)
    : Exception($"Slack {method} failed: {error}")
{
    public string Error => error;

    public TimeSpan? RetryAfter => retryAfter;

    public bool RateLimited => error == "ratelimited";
}
