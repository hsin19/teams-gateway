using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// Teams 的 RichText/Html 訊息內容轉成 Slack 看得順的文字：
/// 回覆引用（blockquote）→ <c>&gt; *發話者*</c> + <c>&gt; 內容</c>、@提及 → <c>*@名字*</c>、區塊標籤 → 換行、去標籤、decode HTML entity。
/// decode 完的內容一律再跳脫 Slack 的控制字元（<c>&amp; &lt; &gt;</c>），不然 Teams 那端打的
/// <c>&lt;!channel&gt;</c>、<c>&lt;https://釣魚|看起來正常的網址&gt;</c> 到 Slack 會變成真的 @channel / 偽裝連結。
/// 純文字訊息沒有標籤 / entity，只會被跳脫。
/// </summary>
public static partial class TeamsText
{
    /// <summary><paramref name="includeQuote"/>=false 時連 blockquote 一起拿掉（呼叫端改用 Slack permalink 連回引用）。</summary>
    public static string ToSlack(string? content, bool includeQuote = true)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content ?? "";

        // 引用跟其餘內文分開轉：各自只 decode 一次、各自跳脫，只有我們加的引用前綴 ">" 保留成 Slack 語法
        var text = new StringBuilder();
        var last = 0;
        foreach (Match match in BlockquoteRegex().Matches(content))
        {
            text.Append(PlainText(content[last..match.Index]));
            if (includeQuote)
                text.Append(Quote(match.Groups[1].Value));
            last = match.Index + match.Length;
        }
        text.Append(PlainText(content[last..]));

        var result = TrailingSpaceRegex().Replace(text.ToString(), "\n");
        result = ExtraBlankRegex().Replace(result, "\n"); // 連續換行收成一個，不留大塊空行
        return result.Trim();
    }

    /// <summary>Slack mrkdwn 的控制字元跳脫（Slack 顯示時會還原成原字元）。</summary>
    public static string EscapeMrkdwn(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // @提及的頭尾記號（控制字元，訊息裡不會出現）：先標起來，等 decode / 跳脫完才換成 Slack 粗體
    private const char MentionStart = '\u0001';
    private const char MentionEnd = '\u0002';

    /// <summary>一段非引用的 HTML → Slack 文字。</summary>
    private static string PlainText(string html)
    {
        var text = MentionRegex().Replace(html, m => $"{MentionStart}{m.Groups[1].Value}{MentionEnd}");
        text = BlockBreakRegex().Replace(text, "\n"); // <br> / </p> / </div> / </li> → 換行
        text = TagRegex().Replace(text, "");          // 其餘標籤去掉
        return Mentions(EscapeMrkdwn(Decode(text)));  // &nbsp; &quot; &#128640; 之類，再跳脫
    }

    /// <summary>
    /// 標起來的 @提及 → Slack 粗體 <c>*@名字*</c>，不然名字混在內文裡看不出是在叫人。
    /// Slack 的粗體前後要是空白或標點才會生效（貼著中文字 / emoji 會直接顯示星號），貼著別的字就補一個空格。
    /// </summary>
    private static string Mentions(string text) => MentionMarkRegex().Replace(text, m =>
    {
        var name = m.Groups[1].Value.Trim();
        if (name.Length == 0)
            return "";
        var end = m.Index + m.Length;
        // 前一個是另一個提及的結尾時不補：那邊已經在後面補過了
        var before = m.Index > 0 && text[m.Index - 1] != MentionEnd && !IsBoldBoundary(text[m.Index - 1]) ? " " : "";
        var after = end < text.Length && !IsBoldBoundary(text[end]) ? " " : "";
        return $"{before}*@{name}*{after}";
    });

    private static bool IsBoldBoundary(char c) => char.IsWhiteSpace(c) || (c < 128 && char.IsPunctuation(c));

    /// <summary>回覆引用：抽出「發話者 + 被引用內容」，渲染成兩行 Slack blockquote。</summary>
    private static string Quote(string inner)
    {
        var author = QuotedAuthor(inner);
        var quoted = QuotedText(inner) ?? Decode(CollapseTags(inner));
        if (quoted.Length == 0 && author is null)
            return "";
        var block = author is null
            ? $"> {EscapeMrkdwn(quoted)}"
            : $"> *{EscapeMrkdwn(author)}*\n> {EscapeMrkdwn(quoted)}";
        return $"\n{block}\n";
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

    /// <summary>
    /// 附件是不是圖片：圖片類型且有 Teams 預覽圖（預覽圖放在 Teams 媒體，跟內嵌圖片一樣能代抓）。
    /// 影片等其他類型就算有預覽縮圖也當一般檔案。
    /// </summary>
    public static bool IsImage(AttachmentDto attachment) =>
        attachment.PreviewUrl is not null && attachment.Type is not null && ImageFileTypes.Contains(attachment.Type);

    private static readonly HashSet<string> ImageFileTypes =
        new(["jpg", "jpeg", "png", "gif", "bmp", "webp", "heic", "heif", "tif", "tiff"], StringComparer.OrdinalIgnoreCase);

    /// <summary>若是回覆，回傳被引用的那則 Teams 訊息 id（blockquote 的 itemid）；不是回覆則 null。</summary>
    public static string? QuotedMessageId(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;
        var m = QuoteIdRegex().Match(content);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>若是回覆，回傳被引用那則的發話者名稱；不是回覆或沒帶名稱則 null。</summary>
    public static string? QuotedAuthorName(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;
        var m = BlockquoteRegex().Match(content);
        return m.Success ? QuotedAuthor(m.Groups[1].Value) : null;
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

    [GeneratedRegex(@"<span\b[^>]*\bitemtype=""http://schema\.skype\.com/Mention""[^>]*>(.*?)</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MentionRegex();

    [GeneratedRegex("\u0001([^\u0002]*)\u0002")]
    private static partial Regex MentionMarkRegex();

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
