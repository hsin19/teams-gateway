using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.Mcp;

/// <summary>
/// MCP tool 的輸出：給 LLM 讀的純文字，一則一行、像 log；程式要結構化資料走 REST <c>/v1</c>（ADR 0005）。
/// Teams 的 RichText/Html 轉成純文字：回覆引用 → <c>[re 作者: "前 60 字…"]</c>、@提及 → <c>@名字</c>、表情符號圖 → 它的 alt、
/// 圖片 → <c>[image #訊息id]</c>、其他附件 → <c>[file: 檔名 #訊息id]</c>（id 給 get_attachments 用）。
/// 跟 relay 的 TeamsText 解析同一種 HTML，但那邊輸出 Slack mrkdwn（要跳脫、有粗體語法），這裡是純文字，所以各寫各的。
/// </summary>
internal static partial class McpText
{
    private const int QuotePreviewLength = 60;
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    // 給 until 用：帶毫秒，同一秒內的訊息才不會被跳過
    private const string CursorFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

    private static readonly HashSet<string> ImageFileTypes =
        new(["jpg", "jpeg", "png", "gif", "bmp", "webp", "heic", "heif", "tif", "tiff"], StringComparer.OrdinalIgnoreCase);

    public static string Transcript(string chatId, MessageWindow window, TimeZoneInfo zone, Func<string, string?>? nameOf = null)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"# {chatId} | {window.Messages.Count} messages | times in {ZoneLabel(zone)}\n");
        foreach (var m in window.Messages)
            text.Append(Line(m, zone, nameOf)).Append('\n');
        if (window.More)
            text.Append(window.Messages.Count > 0
                ? $"# more: until={Format(window.Messages[0].Time, zone, CursorFormat)}\n"
                : "# more: gave up paging before reaching the requested range\n");
        return text.ToString();
    }

    public static string Chats(IReadOnlyList<ConversationDto> chats, TimeZoneInfo zone)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"# {chats.Count} chats | times in {ZoneLabel(zone)}\n");
        foreach (var c in chats.OrderByDescending(c => c.LastMessageTime ?? DateTimeOffset.MinValue))
        {
            var topic = string.IsNullOrWhiteSpace(c.Topic) ? "(no topic)" : c.Topic.Trim();
            var last = c.LastMessageTime is { } t ? Format(t, zone, TimeFormat) : "-";
            text.Append(CultureInfo.InvariantCulture, $"{c.Id} | {topic} | last {last}\n");
        }
        return text.ToString();
    }

    /// <summary>一則一行；內容有多行時後續行縮排兩格，所以新的一則一定從時間開頭。</summary>
    internal static string Line(MessageDto m, TimeZoneInfo zone, Func<string, string?>? nameOf = null)
    {
        var head = $"{Format(m.Time, zone, TimeFormat)} {Sender(m)}";
        if (m.Deleted)
            return $"{head}: (deleted)";
        if (m.Edited)
            head += " (edited)";
        var body = Body(m, nameOf);
        return body.Length == 0 ? $"{head}:" : $"{head}: {body.Replace("\n", "\n  ", StringComparison.Ordinal)}";
    }

    internal static string Body(MessageDto m, Func<string, string?>? nameOf = null)
    {
        var parts = new List<string>();
        var content = (m.Content ?? "").ReplaceLineEndings("\n");
        var html = IsHtml(m);
        if (html)
            foreach (Match q in BlockquoteRegex().Matches(content))
                if (Quote(q.Groups[1].Value, nameOf) is { } quote)
                    parts.Add(quote);
        var images = ImageUrls(m).Count;
        if (images > 0)
            parts.Add(images == 1 ? $"[image #{m.Id}]" : $"[{images} images #{m.Id}]");
        foreach (var file in Files(m))
            parts.Add($"[file: {file.Name} #{m.Id}]");
        var text = html ? PlainText(BlockquoteRegex().Replace(content, "")) : content.Trim();
        if (text.Length > 0)
            parts.Add(text);
        return string.Join(' ', parts);
    }

    /// <summary>
    /// 這則訊息自己的圖片網址，照出現順序：內嵌的 Teams 媒體圖（引用裡的、表情符號的 CDN 圖不算）加上圖片附件的預覽圖。
    /// 只收 gateway 代抓得到的網址（<see cref="MediaClient.IsAllowed"/>）。
    /// </summary>
    public static IReadOnlyList<string> ImageUrls(MessageDto m)
    {
        var urls = new List<string>();
        if (IsHtml(m) && !string.IsNullOrEmpty(m.Content))
            urls.AddRange(ImgSrcRegex().Matches(BlockquoteRegex().Replace(m.Content, ""))
                .Select(x => WebUtility.HtmlDecode(x.Groups[1].Value))
                .Where(url => MediaClient.IsAllowed(url, out _)));
        urls.AddRange((m.Attachments ?? []).Where(IsImage).Select(a => a.PreviewUrl!));
        return urls.Distinct().ToList();
    }

    /// <summary>不是圖片的附件（圖片附件算進 <see cref="ImageUrls"/>）。</summary>
    public static IReadOnlyList<AttachmentDto> Files(MessageDto m) => (m.Attachments ?? []).Where(a => !IsImage(a)).ToList();

    /// <summary>引用裡發話者的帳號（MRI），拿去查現在的名字：HTML 裡寫的名字可能是舊的，甚至是 "Display Name"。</summary>
    public static IEnumerable<string> QuotedAuthorMris(MessageDto m) =>
        IsHtml(m) && !string.IsNullOrEmpty(m.Content)
            ? BlockquoteRegex().Matches(m.Content).Select(q => QuotedAuthorMri(q.Groups[1].Value)).OfType<string>()
            : [];

    /// <summary>時區：UTC offset（<c>+08:00</c>、<c>UTC+8</c>）或 IANA 名稱（要主機有時區資料）。</summary>
    public static TimeZoneInfo ParseZone(string spec)
    {
        spec = spec.Trim();
        if (spec.Length == 0 || spec.Equals("UTC", StringComparison.OrdinalIgnoreCase) || spec.Equals("Z", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;
        if (OffsetRegex().Match(spec) is { Success: true } m)
        {
            var hours = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var minutes = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            if (hours <= 14 && minutes < 60)
            {
                var offset = new TimeSpan(hours, minutes, 0);
                if (m.Groups[1].Value == "-")
                    offset = -offset;
                if (offset == TimeSpan.Zero)
                    return TimeZoneInfo.Utc;
                var id = OffsetLabel(offset);
                return TimeZoneInfo.CreateCustomTimeZone(id, offset, id, id);
            }
        }
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(spec);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new McpException($"Unknown timezone '{spec}': use a UTC offset such as +08:00, or an IANA name such as Asia/Taipei if the server has time zone data");
        }
    }

    /// <summary>
    /// tool 的時間參數：帶 offset 的 ISO 8601（<c>2026-10-27T09:00:00+08:00</c>、結尾 <c>Z</c>），
    /// 或不帶 offset 的 <c>2026-10-27 09:00</c>（當成 <paramref name="zone"/> 的時間）。
    /// </summary>
    public static DateTimeOffset? ParseTime(string? value, TimeZoneInfo zone, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        if (ExplicitOffsetRegex().IsMatch(value))
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
                return exact;
        }
        else if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            try
            {
                return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone));
            }
            catch (ArgumentException)
            {
                // 日光節約跳過、不存在的那段時間：當成讀不懂
            }
        }
        throw new McpException($"Can't read {name} '{value}': use ISO 8601 such as 2026-10-27T09:00:00+08:00, or 2026-10-27 09:00 in the given timezone");
    }

    private static bool IsHtml(MessageDto m) => m.Type != "Text";

    /// <summary>圖片附件：圖片類型而且有預覽圖（預覽圖在 Teams 媒體，代抓得到）；其他附件的預覽圖是空字串。</summary>
    private static bool IsImage(AttachmentDto a) =>
        !string.IsNullOrEmpty(a.PreviewUrl) && a.Type is not null && ImageFileTypes.Contains(a.Type);

    private static string Sender(MessageDto m) =>
        !string.IsNullOrWhiteSpace(m.FromName) ? m.FromName.Trim() : m.From ?? "unknown";

    private static string PlainText(string html)
    {
        var text = EmojiRegex().Replace(html, e => AltRegex().Match(e.Value) is { Success: true } alt ? alt.Groups[1].Value : "");
        text = MentionRegex().Replace(text, m => $"@{m.Groups[1].Value}");
        text = BlockBreakRegex().Replace(text, "\n");
        text = Decode(TagRegex().Replace(text, ""));
        text = TrailingSpaceRegex().Replace(text, "\n");
        return ExtraBlankRegex().Replace(text, "\n").Trim();
    }

    private static string? Quote(string inner, Func<string, string?>? nameOf)
    {
        var author = QuotedAuthorMri(inner) is { } mri && nameOf?.Invoke(mri) is { } known ? known : QuotedAuthor(inner);
        var quoted = Truncate(QuotedText(inner) ?? Decode(CollapseTags(inner)));
        if (quoted.Length == 0 && author is null)
            return null;
        return author is null ? $"[re \"{quoted}\"]" : $"[re {author}: \"{quoted}\"]";
    }

    private static string? QuotedAuthorMri(string inner) =>
        MriTagRegex().Match(inner) is { Success: true } tag && ItemIdRegex().Match(tag.Value) is { Success: true } id ? id.Groups[1].Value : null;

    private static string? QuotedAuthor(string inner)
    {
        var m = MriRegex().Match(inner);
        if (!m.Success)
            return null;
        var author = Decode(CollapseTags(m.Groups[1].Value)).Trim();
        return author.Length == 0 ? null : author;
    }

    private static string? QuotedText(string inner)
    {
        var m = PreviewRegex().Match(inner);
        if (!m.Success)
            return null;
        var preview = Decode(CollapseTags(m.Groups[1].Value)).Trim();
        return preview.Length == 0 ? null : preview;
    }

    private static string Truncate(string s)
    {
        var info = new StringInfo(s);
        return info.LengthInTextElements <= QuotePreviewLength ? s : info.SubstringByTextElements(0, QuotePreviewLength) + "…";
    }

    private static string CollapseTags(string html) =>
        AnyWhitespaceRegex().Replace(TagRegex().Replace(html, ""), " ").Trim();

    private static string Decode(string s) => WebUtility.HtmlDecode(s).Replace(' ', ' ');

    private static string Format(DateTimeOffset t, TimeZoneInfo zone, string format) =>
        TimeZoneInfo.ConvertTime(t, zone).ToString(format, CultureInfo.InvariantCulture);

    private static string ZoneLabel(TimeZoneInfo zone)
    {
        var offset = OffsetLabel(zone.GetUtcOffset(DateTimeOffset.UtcNow));
        return zone.Id == offset || zone.Equals(TimeZoneInfo.Utc) ? offset : $"{zone.Id} ({offset})";
    }

    private static string OffsetLabel(TimeSpan offset) =>
        offset == TimeSpan.Zero ? "UTC" : $"UTC{(offset < TimeSpan.Zero ? '-' : '+')}{offset.Duration():hh\\:mm}";

    [GeneratedRegex(@"<blockquote\b[^>]*>(.*?)</blockquote>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockquoteRegex();

    [GeneratedRegex(@"<img\b[^>]*\bsrc=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrcRegex();

    [GeneratedRegex(@"<img\b[^>]*\bitemtype=""http://schema\.skype\.com/Emoji""[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex EmojiRegex();

    [GeneratedRegex(@"\balt=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex AltRegex();

    [GeneratedRegex(@"itemprop=""mri""[^>]*>([^<]*)", RegexOptions.IgnoreCase)]
    private static partial Regex MriRegex();

    [GeneratedRegex(@"<[^>]*\bitemprop=""mri""[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MriTagRegex();

    [GeneratedRegex(@"\bitemid=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ItemIdRegex();

    [GeneratedRegex(@"itemprop=""preview""[^>]*>(.*?)</", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PreviewRegex();

    [GeneratedRegex(@"<span\b[^>]*\bitemtype=""http://schema\.skype\.com/Mention""[^>]*>(.*?)</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MentionRegex();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex AnyWhitespaceRegex();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpaceRegex();

    [GeneratedRegex(@"\n{2,}")]
    private static partial Regex ExtraBlankRegex();

    [GeneratedRegex(@"^(?:UTC|GMT)?([+-])(\d{1,2})(?::?(\d{2}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetRegex();

    [GeneratedRegex(@"(?:Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitOffsetRegex();
}
