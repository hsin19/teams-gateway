using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// Slack 訊息 → 送到 Teams 的 HTML（ADR 0012）。從 rich_text blocks 轉：樣式、連結、條列、引用、程式碼區塊都有結構，
/// emoji 也直接帶著 unicode，不用自己準備 <c>:smile:</c> → 😄 的對照表；沒有 blocks 才退回 mrkdwn 的 text。
/// 訊息開頭的 @bot 拿掉。
/// </summary>
internal static partial class SlackRichText
{
    /// <summary>訊息是不是 @bot 開頭（@bot 在句子中間的是在討論 bot 本身，不算）。</summary>
    public static bool StartsWithMention(string? text, string botUserId)
    {
        var t = text?.TrimStart() ?? "";
        return t.StartsWith($"<@{botUserId}>", StringComparison.Ordinal) || t.StartsWith($"<@{botUserId}|", StringComparison.Ordinal);
    }

    /// <summary>內容裡 @ 到的人與 # 到的頻道：轉之前先查好名字。</summary>
    public static (HashSet<string> Users, HashSet<string> Channels) References(HistoryMessage message)
    {
        var users = new HashSet<string>();
        var channels = new HashSet<string>();
        if (RichText(message) is { Count: > 0 } blocks)
        {
            foreach (var block in blocks)
                Collect(block, users, channels);
        }
        else
        {
            foreach (Match m in TokenRegex().Matches(message.Text ?? ""))
                if (m.Groups[1].Value is ['@', ..] user)
                    users.Add(user[1..].Split('|')[0]);
                else if (m.Groups[1].Value is ['#', ..] channel)
                    channels.Add(channel[1..].Split('|')[0]);
        }
        return (users, channels);
    }

    /// <summary>轉成 HTML。查不到名字的人 / 頻道用 id。</summary>
    public static string ToHtml(HistoryMessage message, string botUserId, Func<string, string?> userName, Func<string, string?> channelName)
    {
        var renderer = new Renderer(botUserId, userName, channelName);
        if (RichText(message) is { Count: > 0 } blocks)
        {
            foreach (var block in blocks)
                foreach (var element in Elements(block))
                    renderer.Block(element);
        }
        else
        {
            renderer.Mrkdwn(StripMention(message.Text ?? "", botUserId));
        }
        return renderer.ToString();
    }

    /// <summary>HTML 裡有沒有看得到的內容（拿掉開頭的 @bot 後可能什麼都不剩）。</summary>
    public static bool IsBlank(string html) => string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(TagRegex().Replace(html, "")));

    /// <summary>
    /// 確認訊息裡的預覽：原文（拿掉開頭的 @bot）放在 Slack 的引用裡，Slack 自己畫格式與名字。
    /// 太長就截斷：section 上限 3000 字，超過的話整則確認貼不出來。
    /// </summary>
    public static string Preview(string? text, string botUserId, int maxChars = PreviewMaxChars)
    {
        var body = StripMention(text ?? "", botUserId).Trim();
        if (body.Length == 0)
            return "> _(empty)_";
        var quoted = string.Join('\n', body.Split('\n').Select(line => $"> {line}"));
        return quoted.Length <= maxChars ? quoted : $"{quoted[..maxChars].TrimEnd()}…";
    }

    internal const int PreviewMaxChars = 2500;

    internal static string StripMention(string text, string botUserId)
    {
        if (!StartsWithMention(text, botUserId))
            return text;
        var t = text.TrimStart();
        return t[(t.IndexOf('>', StringComparison.Ordinal) + 1)..].TrimStart();
    }

    private static List<JsonElement>? RichText(HistoryMessage message) =>
        message.Blocks is { ValueKind: JsonValueKind.Array } blocks
            ? blocks.EnumerateArray().Where(b => Str(b, "type") == "rich_text").ToList()
            : null;

    private static IEnumerable<JsonElement> Elements(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("elements", out var els) && els.ValueKind == JsonValueKind.Array
            ? els.EnumerateArray()
            : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement e, string style) =>
        e.TryGetProperty("style", out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty(style, out var v) && v.ValueKind == JsonValueKind.True;

    private static void Collect(JsonElement e, HashSet<string> users, HashSet<string> channels)
    {
        if (Str(e, "type") == "user" && Str(e, "user_id") is { } user)
            users.Add(user);
        if (Str(e, "type") == "channel" && Str(e, "channel_id") is { } channel)
            channels.Add(channel);
        foreach (var child in Elements(e))
            Collect(child, users, channels);
    }

    // 不用 WebUtility.HtmlEncode：它會把 emoji 轉成數字 entity
    private static string Escape(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    // Slack 的 text 裡 & < > 已經轉成 entity
    private static string SlackDecode(string s) =>
        s.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);

    /// <summary>
    /// 逐個 block 寫出 HTML。<c>_leading</c>：還在訊息最開頭，這時遇到的 @bot 與空白都拿掉。
    /// </summary>
    private sealed class Renderer(string botUserId, Func<string, string?> userName, Func<string, string?> channelName)
    {
        private readonly StringBuilder _html = new();
        private bool _leading = true;

        public void Block(JsonElement element)
        {
            switch (Str(element, "type"))
            {
                case "rich_text_section":
                    Wrap("p", Inline(element, pre: false));
                    break;
                case "rich_text_quote":
                    Wrap("blockquote", Inline(element, pre: false));
                    break;
                case "rich_text_preformatted":
                    Wrap("pre", Inline(element, pre: true));
                    break;
                case "rich_text_list":
                    var items = Elements(element).Select(item => Inline(item, pre: false)).Where(i => i.Length > 0).ToList();
                    if (items.Count > 0)
                    {
                        var tag = Str(element, "style") == "ordered" ? "ol" : "ul";
                        _html.Append('<').Append(tag).Append('>');
                        foreach (var item in items)
                            _html.Append("<li>").Append(item).Append("</li>");
                        _html.Append("</").Append(tag).Append('>');
                    }
                    break;
            }
        }

        public void Mrkdwn(string text)
        {
            var html = new StringBuilder();
            var last = 0;
            foreach (Match m in TokenRegex().Matches(text))
            {
                html.Append(Plain(text[last..m.Index]));
                html.Append(Token(m.Groups[1].Value));
                last = m.Index + m.Length;
            }
            html.Append(Plain(text[last..]));
            Wrap("p", html.ToString());

            static string Plain(string s) => Escape(SlackDecode(s)).ReplaceLineEndings("<br>");
        }

        public override string ToString() => _html.ToString();

        private void Wrap(string tag, string inner)
        {
            if (inner.Length > 0)
                _html.Append('<').Append(tag).Append('>').Append(inner).Append("</").Append(tag).Append('>');
        }

        private string Inline(JsonElement container, bool pre)
        {
            var html = new StringBuilder();
            foreach (var e in Elements(container))
            {
                var type = Str(e, "type");
                if (_leading)
                {
                    if (type == "user" && Str(e, "user_id") == botUserId)
                        continue;
                    if (type == "text" && string.IsNullOrWhiteSpace(Str(e, "text")))
                        continue;
                }
                var piece = type switch
                {
                    "text" => Text(e, pre),
                    "link" => Link(e),
                    "emoji" => Emoji(e),
                    "user" => "@" + Escape(Str(e, "user_id") is { } u ? userName(u) ?? u : "unknown"),
                    "channel" => "#" + Escape(Str(e, "channel_id") is { } c ? channelName(c) ?? c : "unknown"),
                    "broadcast" => "@" + Escape(Str(e, "range") ?? "here"),
                    "usergroup" => "@" + Escape(Str(e, "usergroup_id") ?? "group"),
                    "date" => Escape(Str(e, "fallback") ?? ""),
                    "color" => Escape(Str(e, "value") ?? ""),
                    _ => "",
                };
                if (piece.Length > 0)
                    _leading = false;
                html.Append(piece);
            }
            return html.ToString();
        }

        private string Text(JsonElement e, bool pre)
        {
            var raw = Str(e, "text") ?? "";
            if (_leading)
                raw = raw.TrimStart();
            var t = Escape(raw);
            if (!pre)
                t = t.ReplaceLineEndings("<br>");
            if (Flag(e, "code"))
                t = $"<code>{t}</code>";
            if (Flag(e, "bold"))
                t = $"<b>{t}</b>";
            if (Flag(e, "italic"))
                t = $"<i>{t}</i>";
            if (Flag(e, "strike"))
                t = $"<s>{t}</s>";
            return t;
        }

        private static string Link(JsonElement e)
        {
            var url = Str(e, "url") ?? "";
            var label = Str(e, "text") is { Length: > 0 } text ? text : url;
            return url.Length == 0 ? Escape(label) : $"<a href=\"{Escape(url)}\">{Escape(label)}</a>";
        }

        // unicode 是連字號分隔的 16 進位碼位（例如 1f44d-1f3fb）；workspace 自訂的表情沒有，留 :name:
        private static string Emoji(JsonElement e)
        {
            if (Str(e, "unicode") is { Length: > 0 } unicode)
            {
                try
                {
                    return string.Concat(unicode.Split('-').Select(h => char.ConvertFromUtf32(int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException)
                {
                    // 看不懂就退回名字
                }
            }
            return $":{Escape(Str(e, "name") ?? "")}:";
        }

        // mrkdwn 的 <…>：<@U…>、<#C…|名字>、<!here>、<網址|文字>
        private string Token(string token) => token switch
        {
            ['@', .. var user] => "@" + Escape(userName(user.Split('|')[0]) ?? user.Split('|')[0]),
            ['#', .. var channel] => "#" + Escape(channel.Split('|') is [_, var name] ? name : channelName(channel) ?? channel),
            ['!', .. var special] => "@" + Escape(special.Split('|')[0]),
            _ => token.Split('|', 2) is [var url, var label]
                ? $"<a href=\"{Escape(SlackDecode(url))}\">{Escape(SlackDecode(label))}</a>"
                : $"<a href=\"{Escape(SlackDecode(token))}\">{Escape(SlackDecode(token))}</a>",
        };
    }

    [GeneratedRegex("<([^<>]+)>")]
    private static partial Regex TokenRegex();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagRegex();
}
