using System.Net;
using System.Text.RegularExpressions;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// Teams 的 RichText/Html 訊息內容轉成 Slack 看得順的文字：
/// 回覆引用（blockquote）→ <c>&gt; </c>、區塊標籤 → 換行、去掉其餘標籤、decode HTML entity。
/// 純文字訊息沒有標籤 / entity，等於原樣通過。
/// </summary>
public static partial class TeamsText
{
    public static string ToSlack(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content ?? "";

        // 回覆引用：抽出 blockquote 內文（去標籤、收成一行），整段前綴 "> "
        var text = BlockquoteRegex().Replace(content, match =>
        {
            var inner = AnyWhitespaceRegex().Replace(TagRegex().Replace(match.Groups[1].Value, ""), " ").Trim();
            return inner.Length == 0 ? "" : $"\n> {inner}\n";
        });

        text = BlockBreakRegex().Replace(text, "\n"); // <br> / </p> / </div> / </li> → 換行
        text = TagRegex().Replace(text, "");          // 其餘標籤去掉
        text = WebUtility.HtmlDecode(text);           // &nbsp; &quot; &#128640; 之類
        text = text.Replace(' ', ' ');           // nbsp → 一般空白

        text = TrailingSpaceRegex().Replace(text, "\n");
        text = ExtraBlankRegex().Replace(text, "\n"); // 連續換行收成一個，不留大塊空行
        return text.Trim();
    }

    [GeneratedRegex(@"<blockquote\b[^>]*>(.*?)</blockquote>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockquoteRegex();

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
