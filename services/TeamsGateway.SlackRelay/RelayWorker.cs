using System.Globalization;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// Phase 1：從 <see cref="IEventSource"/> 逐筆收 gateway 的事件，把 Teams 訊息轉貼進對應 Slack 頻道。
/// 游標、續接、重連都在 source 裡（ADR 0006）；這裡處理完一筆才拿下一筆。
/// </summary>
public sealed class RelayWorker(
    IEventSource source,
    SlackClient slack,
    GatewayMediaClient media,
    MessageLinks links,
    IOptions<RelayOptions> options,
    ILogger<RelayWorker> logger) : BackgroundService
{
    private const int MaxImagesPerMessage = 10;

    // Teams 圖片網址 → 已上傳的 Slack file id：編輯時同一張圖不重傳（訊息是逐則處理，不用 concurrent）。
    // 滿了就整個清掉：代價只是很舊的訊息被編輯時圖再傳一次，換來常駐時記憶體不會一直長
    private readonly Dictionary<string, string> _uploadedImages = new();
    private const int MaxUploadedImages = 2000;

    // Teams 帳號（MRI）→ 最近看到的顯示名稱。引用 HTML 裡的作者名不可靠（可能是舊名或 "Display Name"），
    // Teams 自己也是拿 MRI 查名字。純記憶體：重啟後要等那個人講過話才查得到，查不到就退回 HTML 裡的名字
    private readonly Dictionary<string, string> _senderNames = new();
    private const int MaxSenderNames = 5000;

    // Slack thread（channel/ts）→ 裡面已經貼過的表情留言的事件序號。relay 重啟後從 afterMessage 續接會重送表情事件，
    // 表情沒有版本號，靠序號認出貼過的。第一次碰到某個 thread 才去 Slack 讀；滿了整個清掉，代價只是再讀一次
    private readonly Dictionary<string, HashSet<long>> _reactionNotes = new();
    private const int MaxReactionThreads = 2000;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var routes = BuildRoutes(opts.Slack.Routes, logger);
        if (!HasDestination(opts.Slack, routes))
        {
            logger.LogError("未設定 Relay:Slack:BotToken 或任何頻道，relay 不啟動");
            return;
        }

        // 有路由就只訂閱那些對話；否則訂全部（走 DefaultChannel）
        IReadOnlyCollection<string>? chats = routes.Count > 0 ? routes.Keys : null;
        logger.LogInformation("relay 啟動：{Count} 條路由，default={Default}", routes.Count, opts.Slack.DefaultChannel ?? "(無)");

        // 從 Slack 歷史回補近期的訊息對應（metadata 夾著 Teams id），重啟後才連得回舊訊息的回覆 / 編輯；
        // SSE source 剛啟動時也靠它決定從哪則之後續接，所以要先做完
        await WarmupLinksAsync(routes, opts.Slack.DefaultChannel, opts.Links.RetentionHours, ct);

        // 單則的失敗在 WithRetryAsync 裡吞掉，不會冒出來中斷串流
        await foreach (var evt in source.ReadAsync(chats, ct))
        {
            if (evt.Message is { } message)
                await RelayWithRetryAsync(message, routes, opts.Slack.DefaultChannel, ct);
            else if (evt.Reaction is { } reaction)
                await WithRetryAsync($"reaction {reaction.Emoji} (message {reaction.MessageId})", () => RelayReactionAsync(reaction, Seq(evt.Id), ct), ct);
        }
    }

    /// <summary>worker 要不要啟動、<c>/health</c> 算不算在轉送，用同一個條件。</summary>
    internal static bool HasDestination(RelayOptions.SlackSettings slack, Dictionary<string, string> routes) =>
        !string.IsNullOrWhiteSpace(slack.BotToken) && (slack.DefaultChannel is not null || routes.Count > 0);

    /// <summary>
    /// Routes → 對照表。同一個 Teams 對話設了兩次以後面為準並警告（用 ToDictionary 會丟例外、整個 relay 起不來），
    /// 缺 chat / channel 的略過。
    /// </summary>
    internal static Dictionary<string, string> BuildRoutes(IEnumerable<RelayOptions.ChannelRoute> routes, ILogger logger)
    {
        var map = new Dictionary<string, string>();
        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.Chat) || string.IsNullOrWhiteSpace(route.Channel))
            {
                logger.LogWarning("略過不完整的路由：chat={Chat} channel={Channel}", route.Chat, route.Channel);
                continue;
            }
            if (map.TryGetValue(route.Chat, out var existing) && existing != route.Channel)
                logger.LogWarning("路由重複：{Chat} 同時設了 {First} 和 {Second}，用後者", route.Chat, existing, route.Channel);
            map[route.Chat] = route.Channel;
        }
        return map;
    }

    /// <summary>單則轉送失敗後的重試間隔；用完還失敗就放棄這則。</summary>
    internal TimeSpan[] RetryDelays { get; init; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    /// <summary>Slack 限流卻沒說要等多久時等這麼久。</summary>
    internal TimeSpan RateLimitFallback { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 轉一則；Slack / gateway 一時不通就退避重試，還是不行記 error 放棄這則、繼續下一則。
    /// 被 Slack 限流不算重試次數，照 Retry-After 等到能送為止：限流一定會過去，放棄就是掉訊息
    /// （睡眠醒來補的一批訊息很容易撞到每頻道約每秒一則的上限）。
    /// 例外不能冒出去：外層會當成 SSE 斷線，重連後這則就默默消失、log 還寫成連線問題。
    /// （編輯在「舊版留證」成功、「原地更新」失敗時重試會多留一則證，可以接受。）
    /// </summary>
    internal Task RelayWithRetryAsync(MessageDto message, Dictionary<string, string> routes, string? defaultChannel, CancellationToken ct) =>
        WithRetryAsync($"message {message.Id} ({message.ConversationId})", () => RelayAsync(message, routes, defaultChannel, ct), ct);

    private async Task WithRetryAsync(string what, Func<Task> relay, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                await relay();
                return;
            }
            catch (SlackTransientException ex) when (ex.RateLimited && !ct.IsCancellationRequested)
            {
                var wait = ex.RetryAfter ?? RateLimitFallback;
                logger.LogWarning("Slack rate limited while relaying {What}; retrying in {Delay}", what, wait);
                await Task.Delay(wait, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < RetryDelays.Length)
            {
                logger.LogWarning(ex, "Relaying {What} failed; retrying in {Delay}", what, RetryDelays[attempt]);
                await Task.Delay(RetryDelays[attempt++], ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Relaying {What} still failed after {Attempts} retries; giving up", what, RetryDelays.Length);
                return;
            }
        }
    }

    /// <summary>
    /// 有人在 Teams 按 / 收回表情：在那則 Slack 訊息的 thread 留一句「*誰* reacted 👀」/「removed」，
    /// bot 也在原訊息按上同一個 Slack 表情（頻道裡就看得到有人按），最後一個人收回時拿掉。
    /// 那則沒轉過（或太舊、對應已清掉）就沒地方掛，略過。
    /// <paramref name="seq"/> 是 gateway 的事件序號：thread 裡已經有同一個序號的留言（relay 重啟後續接重送的）就不再貼；
    /// 原生表情的按 / 收回本來就冪等，照做。
    /// </summary>
    internal async Task RelayReactionAsync(ReactionDto reaction, long? seq, CancellationToken ct)
    {
        if (reaction.UserName is { } known)
            RememberSender(reaction.UserMri, known);
        if (links.Lookup(reaction.MessageId) is not { } target)
            return;

        var notes = seq is null ? null : await ReactionNotesAsync(target.Channel, target.Ts, ct);
        if (notes is null || !notes.Contains(seq.GetValueOrDefault()))
        {
            var who = reaction.UserName ?? SenderName(reaction.UserMri) ?? "Someone";
            var verb = reaction.Added ? "reacted" : "removed";
            // 貼成了才記：沒貼成的下次重送還會再試
            if (await slack.PostReactionNoteAsync(target.Channel, target.Ts,
                    $"*{TeamsText.EscapeMrkdwn(who)}* {verb} {TeamsText.EscapeMrkdwn(TeamsEmoji.Glyph(reaction.Emoji))}", seq, ct))
                notes?.Add(seq.GetValueOrDefault());
        }

        // 不同 Teams 表情可能對到同一個 Slack 表情（like / yes 都是 👍）：其中一個收回到 0 就會拿掉，可以接受
        if (TeamsEmoji.SlackName(reaction.Emoji) is { } name && (reaction.Added || reaction.Count == 0))
            await slack.ReactAsync(target.Channel, target.Ts, name, add: reaction.Added, ct);
    }

    private async Task RelayAsync(MessageDto message, Dictionary<string, string> routes, string? defaultChannel, CancellationToken ct)
    {
        RememberSender(message); // 先記：回覆引用自己的訊息時，這一則就查得到
        var channel = routes.GetValueOrDefault(message.ConversationId) ?? defaultChannel;
        if (channel is null)
            return; // 沒對應頻道、也沒 default

        var version = message.Version > 0 ? message.Version : (long?)null;

        // 這則我們轉過 → 編輯 / 刪除：原地更新，舊版進 thread 留證（不加 (edited)，靠 Slack 原生標記）
        if (links.Lookup(message.Id) is { } prior)
        {
            if (!IsNewer(message, prior.Version))
                return; // 重送（斷線補收等），Slack 上已經是這一版：不要再留證一次
            await slack.PostReplyAsync(prior.Channel, prior.Ts, $"{VersionLabel(message.Id, prior.Version)}\n{prior.Text}", prior.Images, ct);
            var updated = message.Deleted
                ? new SlackContent("_(message deleted)_", []) // 誰說的、原本寫什麼，在上面那則 thread 留證裡
                : await FormatAsync(message, prior.Channel, ct);
            // 更新被拒（SlackClient 已記 error）時 Slack 上還是舊版：對照表也留舊版，不然之後會以為已經更新過
            if (await slack.UpdateMessageAsync(prior.Channel, prior.Ts, updated.Text, message.Id, version, updated.Images, ct, message.ConversationId))
                links.Remember(message.Id, prior.Channel, prior.Ts, updated.Text, version, updated.Images, message.ConversationId);
            return;
        }

        if (string.IsNullOrEmpty(message.Content) && message.Attachments is not { Count: > 0 })
            return; // 新訊息但無文字也無附件（純系統事件），不轉

        var content = await FormatAsync(message, channel, ct);
        var ts = await slack.PostMessageAsync(channel, content.Text, message.Id, version, content.Images, ct, message.ConversationId);
        if (ts is not null)
            links.Remember(message.Id, channel, ts, content.Text, version, content.Images, message.ConversationId); // 記住對應，給之後的回覆 / 編輯用
    }

    /// <summary>
    /// 轉過的訊息又來一次時，是真的有新版（編輯 / 刪除）還是重送。
    /// Slack 上那則沒記版本（這功能上線前轉的）時，只有帶編輯 / 刪除標記才當新版。
    /// </summary>
    public static bool IsNewer(MessageDto message, long? shownVersion) =>
        shownVersion is { } shown ? message.Version > shown : message.Edited || message.Deleted;

    /// <summary>
    /// thread 留證的標題：那一版是原文還是編輯、什麼時候寫的。連續編輯時一眼看出新舊。
    /// 時間用 Slack 的 <c>&lt;!date&gt;</c>，依看的人的時區顯示、精確到秒（同一分鐘內連改也分得出）。
    /// </summary>
    public static string VersionLabel(string messageId, long? version)
    {
        if (version is not { } v)
            return "_Previous version_";
        var kind = v.ToString(CultureInfo.InvariantCulture) == messageId ? "Original" : "Edited";
        var fallback = DateTimeOffset.FromUnixTimeMilliseconds(v).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        return $"_{kind}_ · <!date^{v / 1000}^{{date_short_pretty}} {{time_secs}}|{fallback}>";
    }

    /// <summary>加入各頻道並讀近 <paramref name="retentionHours"/> 的歷史，從 metadata 把 Teams id → Slack 訊息 撈回對應表。</summary>
    internal async Task WarmupLinksAsync(Dictionary<string, string> routes, string? defaultChannel, int retentionHours, CancellationToken ct)
    {
        var channels = new HashSet<string>(routes.Values);
        if (!string.IsNullOrWhiteSpace(defaultChannel))
            channels.Add(defaultChannel);
        if (channels.Count == 0)
            return;

        var oldest = DateTimeOffset.UtcNow.AddHours(-retentionHours).ToUnixTimeSeconds();
        foreach (var channel in channels)
        {
            try
            {
                await slack.JoinChannelAsync(channel, ct); // 要是成員才讀得到歷史
                var recent = await slack.ReadRelayedAsync(channel, oldest, ct);
                foreach (var m in recent)
                    links.Remember(m.TeamsId, channel, m.Ts, m.Text, m.Version, chat: m.Chat);
                // 近期沒轉過東西：往更早找最新一則當續接點，不然重啟後從「現在」開始，停機期間 gateway 發出的事件就漏了
                if (recent.Count == 0 && await slack.ReadNewestRelayedAsync(channel, ct) is { } newest)
                    links.Remember(newest.TeamsId, channel, newest.Ts, newest.Text, newest.Version, chat: newest.Chat);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "回補 {Channel} 的訊息對應失敗（可能 app 還沒加 channels:history/join 權限）", channel);
            }
        }
        logger.LogInformation("從 Slack 回補 {Count} 筆訊息對應（近 {Hours}h）", links.Count, retentionHours);
    }

    private static string Header(MessageDto message) => $"{Name(message)}:";

    // 顯示名稱是對方自己取的，要跳脫，不然名稱裡的 <!channel> 之類會變成 Slack 語法
    private static string Name(MessageDto message) =>
        $"*{TeamsText.EscapeMrkdwn(string.IsNullOrWhiteSpace(message.FromName) ? message.From ?? "unknown" : message.FromName)}*";

    private void RememberSender(MessageDto message)
    {
        if (message.From is { Length: > 0 } mri && !string.IsNullOrWhiteSpace(message.FromName))
            RememberSender(mri, message.FromName);
    }

    private void RememberSender(string mri, string name)
    {
        if (_senderNames.Count >= MaxSenderNames && !_senderNames.ContainsKey(mri))
            _senderNames.Clear(); // 滿了整個清掉：常駐很久也不會一直長，代價只是暫時退回 HTML 裡的名字
        _senderNames[mri] = name;
    }

    private string? SenderName(string mri) => _senderNames.GetValueOrDefault(mri);

    /// <summary>某則 Slack 訊息 thread 裡已經貼過的表情留言序號；第一次碰到這個 thread 才去 Slack 讀，之後用記憶體裡的。</summary>
    private async Task<HashSet<long>> ReactionNotesAsync(string channel, string ts, CancellationToken ct)
    {
        var key = $"{channel}/{ts}";
        if (_reactionNotes.TryGetValue(key, out var notes))
            return notes;
        if (_reactionNotes.Count >= MaxReactionThreads)
            _reactionNotes.Clear();
        notes = [.. await slack.ReadReactionSeqsAsync(channel, ts, ct)];
        _reactionNotes[key] = notes;
        return notes;
    }

    /// <summary>SSE 的 <c>id</c>（gateway 事件序號）；沒有或看不懂就 null，表情留言就不去重。</summary>
    private static long? Seq(string? eventId) =>
        long.TryParse(eventId, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) ? seq : null;

    /// <summary>發話者那行 + 內文：內文一律另起一行（單行、多行、回覆看起來都一致）；沒內文（只有圖 / 附件）就只有發話者。</summary>
    internal static string Compose(string header, string body) => body.Length > 0 ? $"{header}\n{body}" : header;

    private async Task<SlackContent> FormatAsync(MessageDto message, string channel, CancellationToken ct)
    {
        // 回覆：被引用的每一則若我們轉過，就在第一行接一個指向它的 permalink（Slack 會 unfurl 成原生引用）、不再畫 blockquote；
        // 沒轉過的照樣畫成 blockquote。連結用作者名當標籤，不貼裸網址
        var replyLinks = new List<string>();
        var linkedQuotes = new HashSet<string>();
        foreach (var quote in TeamsText.Quotes(message.Content))
        {
            if (links.Lookup(quote.Id) is { } slackRef
                && await slack.GetPermalinkAsync(slackRef.Channel, slackRef.Ts, ct) is { } permalink)
            {
                var author = (quote.AuthorMri is { } mri ? SenderName(mri) : null) ?? quote.Author ?? "message";
                replyLinks.Add($"<{permalink}|{TeamsText.EscapeMrkdwn(author)}>");
                linkedQuotes.Add(quote.Id);
            }
        }

        var body = TeamsText.ToSlack(message.Content, linkedQuotes, SenderName);
        // 回覆的第一行讀起來是一句話：「*Bob* replied to Alice, Carol」（不加冒號），內容在下一行
        var header = replyLinks.Count == 0 ? Header(message) : $"{Name(message)} replied to {string.Join(", ", replyLinks)}";

        var text = Compose(header, body).TrimEnd();

        // 圖片（內嵌的 + 附件裡的）貼成 image block；其他附件列成連結
        var attachments = message.Attachments ?? [];
        var imageUrls = TeamsText.ImageUrls(message.Content)
            .Concat(attachments.Where(TeamsText.IsImage).Select(a => a.PreviewUrl!))
            .Distinct()
            .ToList();
        var files = attachments.Where(a => !TeamsText.IsImage(a)).ToList();
        if (files.Count > 0)
            text += $"\n{(files.Count == 1 ? "Attachment" : "Attachments")}: " + string.Join(", ", files.Select(FileLink));

        var (images, failed) = await UploadImagesAsync(imageUrls, ct);
        if (failed > 0)
            text += $"\n_({failed} image{(failed == 1 ? "" : "s")} could not be forwarded)_";
        return new SlackContent(text, images);
    }

    private static string FileLink(AttachmentDto file) =>
        file.ShareUrl is null ? TeamsText.EscapeMrkdwn(file.Name) : $"<{file.ShareUrl}|{TeamsText.EscapeMrkdwn(file.Name)}>";

    /// <summary>圖片經 gateway 抓下來、上傳 Slack，回傳 file id 與失敗張數；同一張圖（編輯時）不重傳。</summary>
    private async Task<(IReadOnlyList<string> FileIds, int Failed)> UploadImagesAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        var fileIds = new List<string>();
        foreach (var url in urls.Take(MaxImagesPerMessage))
        {
            if (!_uploadedImages.TryGetValue(url, out var fileId))
            {
                fileId = await UploadImageAsync(url, ct);
                if (fileId is null)
                    continue;
                if (_uploadedImages.Count >= MaxUploadedImages)
                    _uploadedImages.Clear();
                _uploadedImages[url] = fileId;
            }
            fileIds.Add(fileId);
        }
        return (fileIds, urls.Count - fileIds.Count);
    }

    private async Task<string?> UploadImageAsync(string url, CancellationToken ct)
    {
        try
        {
            if (await media.GetAsync(url, ct) is not { } image)
                return null;
            if (ImageExtension(image.ContentType) is not { } ext)
            {
                logger.LogWarning("圖片格式 {ContentType} 不能用 Slack image block 顯示，略過", image.ContentType);
                return null;
            }
            return await slack.UploadFileAsync(image.Bytes, $"teams-image{ext}", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "轉送圖片失敗：{Url}", url);
            return null;
        }
    }

    // image block 的 slack_file 只支援 png / jpg / gif
    private static string? ImageExtension(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        _ => null,
    };

    /// <summary>要貼到 Slack 的內容：文字 + 已上傳圖片的 Slack file id。</summary>
    private readonly record struct SlackContent(string Text, IReadOnlyList<string> Images);
}
