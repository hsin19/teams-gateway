using System.Net;
using System.Text.RegularExpressions;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// Teams 的 RichText/Html 訊息內容轉成 Slack 看得順的文字：
/// 回覆引用（blockquote）→ <c>&gt; *發話者*</c> + <c>&gt; 內容</c>、區塊標籤 → 換行、去標籤、decode HTML entity。
/// 純文字訊息沒有標籤 / entity，等於原樣通過。
/// </summary>
public static partial class TeamsText
{
    /// <summary><paramref name="includeQuote"/>=false 時連 blockquote 一起拿掉（呼叫端改用 Slack permalink 連回引用）。</summary>
    public static string ToSlack(string? content, bool includeQuote = true)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content ?? "";

        // 回覆引用：抽出「發話者 + 被引用內容」，渲染成兩行 Slack blockquote（或整個拿掉）
        var text = BlockquoteRegex().Replace(content, match =>
        {
            if (!includeQuote)
                return "";
            var inner = match.Groups[1].Value;
            var author = QuotedAuthor(inner);
            var quoted = QuotedText(inner) ?? Decode(CollapseTags(inner));
            if (quoted.Length == 0 && author is null)
                return "";
            var block = author is null ? $"> {quoted}" : $"> *{author}*\n> {quoted}";
            return $"\n{block}\n";
        });

        text = BlockBreakRegex().Replace(text, "\n"); // <br> / </p> / </div> / </li> → 換行
        text = TagRegex().Replace(text, "");          // 其餘標籤去掉
        text = Decode(text);                          // &nbsp; &quot; &#128640; 之類

        text = TrailingSpaceRegex().Replace(text, "\n");
        text = ExtraBlankRegex().Replace(text, "\n"); // 連續換行收成一個，不留大塊空行
        return text.Trim();
    }

    /// <summary>
    /// 這則訊息自己的內嵌圖片網址（Teams 媒體 <c>*.asm.skype.com</c>），照出現順序。
    /// blockquote 裡被引用訊息的圖不算；表情符號那類 CDN 圖片也不算。
    /// </summary>
    public static IReadOnlyList<string> ImageUrls(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return [];
        var own = BlockquoteRegex().Replace(content, "");
        return ImgSrcRegex().Matches(own)
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value))
            .Where(url => url.Contains(".asm.skype.com/", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
    }

    /// <summary>若是回覆，回傳被引用的那則 Teams 訊息 id（blockquote 的 itemid）；不是回覆則 null。</summary>
    public static string? QuotedMessageId(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;
        var m = QuoteIdRegex().Match(content);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>引用的發話者（itemprop="mri" 元素的文字）。要完整 decode + trim，不然尾端空白會讓 Slack 粗體失效。</summary>
    private static string? QuotedAuthor(string inner)
    {
        var m = MriRegex().Match(inner);
        if (!m.Success)
            return null;
        var author = Decode(CollapseTags(m.Groups[1].Value)).Trim();
        return author.Length == 0 ? null : author;
    }

    /// <summary>被引用的內容（itemprop="preview" 元素的文字）。</summary>
    private static string? QuotedText(string inner)
    {
        var m = PreviewRegex().Match(inner);
        if (!m.Success)
            return null;
        var preview = Decode(CollapseTags(m.Groups[1].Value)).Trim();
        return preview.Length == 0 ? null : preview;
    }

    private static string CollapseTags(string html) =>
        AnyWhitespaceRegex().Replace(TagRegex().Replace(html, ""), " ").Trim();

    private static string Decode(string s) => WebUtility.HtmlDecode(s).Replace(' ', ' ');

    [GeneratedRegex(@"<blockquote\b[^>]*>(.*?)</blockquote>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockquoteRegex();

    [GeneratedRegex(@"<blockquote\b[^>]*\bitemid=""(\d+)""", RegexOptions.IgnoreCase)]
    private static partial Regex QuoteIdRegex();

    [GeneratedRegex(@"<img\b[^>]*\bsrc=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrcRegex();

    [GeneratedRegex(@"itemprop=""mri""[^>]*>([^<]*)", RegexOptions.IgnoreCase)]
    private static partial Regex MriRegex();

    [GeneratedRegex(@"itemprop=""preview""[^>]*>(.*?)</", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PreviewRegex();

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
}
