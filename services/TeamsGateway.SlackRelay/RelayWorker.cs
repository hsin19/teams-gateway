using System.Globalization;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// Phase 1：訂閱 gateway SSE，把 Teams 訊息轉貼進對應 Slack 頻道。
/// 斷線用 <c>Last-Event-ID</c>（= 最後收到的事件序號）續接，指數退避重連。
/// </summary>
public sealed class RelayWorker(
    GatewayEventStream stream,
    SlackClient slack,
    GatewayMediaClient media,
    MessageLinks links,
    IOptions<RelayOptions> options,
    ILogger<RelayWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private const int MaxImagesPerMessage = 10;

    // Teams 圖片網址 → 已上傳的 Slack file id：編輯時同一張圖不重傳（訊息是逐則處理，不用 concurrent）。
    // 滿了就整個清掉：代價只是很舊的訊息被編輯時圖再傳一次，換來常駐時記憶體不會一直長
    private readonly Dictionary<string, string> _uploadedImages = new();
    private const int MaxUploadedImages = 2000;

    // Teams 帳號（MRI）→ 最近看到的顯示名稱。引用 HTML 裡的作者名不可靠（可能是舊名或 "Display Name"），
    // Teams 自己也是拿 MRI 查名字。純記憶體：重啟後要等那個人講過話才查得到，查不到就退回 HTML 裡的名字
    private readonly Dictionary<string, string> _senderNames = new();
    private const int MaxSenderNames = 5000;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var routes = BuildRoutes(opts.Slack.Routes, logger);
        if (string.IsNullOrWhiteSpace(opts.Slack.BotToken)
            || (opts.Slack.DefaultChannel is null && routes.Count == 0))
        {
            logger.LogError("未設定 Relay:Slack:BotToken 或任何頻道，relay 不啟動");
            return;
        }

        // 有路由就只訂閱那些對話；否則訂全部（走 DefaultChannel）
        var chats = routes.Count > 0 ? string.Join(',', routes.Keys) : null;
        logger.LogInformation("relay 啟動：{Count} 條路由，default={Default}", routes.Count, opts.Slack.DefaultChannel ?? "(無)");

        // 從 Slack 歷史回補近期的訊息對應（metadata 夾著 Teams id），重啟後才連得回舊訊息的回覆 / 編輯
        await WarmupLinksAsync(routes, opts.Slack.DefaultChannel, opts.Links.RetentionHours, ct);

        var backoff = MinBackoff;
        string? lastId = null;
        // 剛重啟沒有事件序號：從 Slack 上最後一則轉過去的訊息之後續接，停機期間的不會漏（重送的照版本去重）
        var resumeAfter = links.NewestTeamsId();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var evt in stream.ConnectAsync(chats, lastId, resumeAfter, ct))
                {
                    await RelayWithRetryAsync(evt.Message, routes, opts.Slack.DefaultChannel, ct);
                    lastId = evt.Id ?? lastId; // 處理完（或放棄）才往前推
                    backoff = MinBackoff; // 收得到就重置退避
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "gateway SSE 連線中斷，{Seconds}s 後重連", backoff.TotalSeconds);
            }

            await Task.Delay(backoff, ct);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
        }
    }

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

    /// <summary>
    /// 轉一則；Slack / gateway 一時不通就退避重試，還是不行記 error 放棄這則、繼續下一則。
    /// 例外不能冒出去：外層會當成 SSE 斷線，重連後這則就默默消失、log 還寫成連線問題。
    /// （編輯在「舊版留證」成功、「原地更新」失敗時重試會多留一則證，可以接受。）
    /// </summary>
    internal async Task RelayWithRetryAsync(MessageDto message, Dictionary<string, string> routes, string? defaultChannel, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await RelayAsync(message, routes, defaultChannel, ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < RetryDelays.Length)
            {
                logger.LogWarning(ex, "轉送 {MessageId} 失敗，{Delay} 後重試", message.Id, RetryDelays[attempt]);
                await Task.Delay(RetryDelays[attempt], ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "轉送 {MessageId}（{Conversation}）重試 {Attempts} 次仍失敗，放棄這則",
                    message.Id, message.ConversationId, RetryDelays.Length);
                return;
            }
        }
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
            await slack.UpdateMessageAsync(prior.Channel, prior.Ts, updated.Text, message.Id, version, updated.Images, ct);
            links.Remember(message.Id, prior.Channel, prior.Ts, updated.Text, version, updated.Images);
            return;
        }

        if (string.IsNullOrEmpty(message.Content) && message.Attachments is not { Count: > 0 })
            return; // 新訊息但無文字也無附件（純系統事件），不轉

        var content = await FormatAsync(message, channel, ct);
        var ts = await slack.PostMessageAsync(channel, content.Text, message.Id, version, content.Images, ct);
        if (ts is not null)
            links.Remember(message.Id, channel, ts, content.Text, version, content.Images); // 記住對應，給之後的回覆 / 編輯用
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
    private async Task WarmupLinksAsync(Dictionary<string, string> routes, string? defaultChannel, int retentionHours, CancellationToken ct)
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
                foreach (var m in await slack.ReadRelayedAsync(channel, oldest, ct))
                    links.Remember(m.TeamsId, channel, m.Ts, m.Text, m.Version);
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
        if (message.From is not { Length: > 0 } mri || string.IsNullOrWhiteSpace(message.FromName))
            return;
        if (_senderNames.Count >= MaxSenderNames && !_senderNames.ContainsKey(mri))
            _senderNames.Clear(); // 滿了整個清掉：常駐很久也不會一直長，代價只是暫時退回 HTML 裡的名字
        _senderNames[mri] = message.FromName;
    }

    private string? SenderName(string mri) => _senderNames.GetValueOrDefault(mri);

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
