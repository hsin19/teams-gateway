using System.Globalization;
using System.Text.RegularExpressions;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// Teams 的表情代碼 → 顯示用的 Unicode 字元，以及 Slack 原生表情的名稱（bot 按 reaction 用）。
/// 代碼有兩種：Skype 時代的名字（<c>heart</c>、<c>like</c>、<c>fistbump</c>…），和「Unicode 碼位_名稱」（<c>1f440_eyes</c>）；
/// 都可能帶膚色後綴 <c>-tone1</c>～<c>-tone5</c>。對照表是照實際出現過的代碼整理的，對不到的：
/// 顯示用原始代碼，Slack 名稱就不給（不按原生表情）。
/// </summary>
public static partial class TeamsEmoji
{
    // Skype 名字 → (Unicode, Slack 名稱)
    private static readonly Dictionary<string, (string Glyph, string Slack)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["like"] = ("👍", "+1"),
        ["yes"] = ("👍", "+1"),
        ["heart"] = ("❤️", "heart"),
        ["laugh"] = ("😆", "laughing"),
        ["surprised"] = ("😮", "open_mouth"),
        ["sad"] = ("😢", "cry"),
        ["angry"] = ("😠", "angry"),
        ["ok"] = ("👌", "ok_hand"),
        ["bow"] = ("🙇", "bow"),
        ["bowing"] = ("🙇", "bow"),
        ["thanks"] = ("🙏", "pray"),
        ["praying"] = ("🙏", "pray"),
        ["goodluck"] = ("🍀", "four_leaf_clover"),
        ["fistbump"] = ("👊", "punch"),
        ["angel"] = ("😇", "innocent"),
        ["vulcansalute"] = ("🖖", "spock-hand"),
        ["handshake"] = ("🤝", "handshake"),
        ["crossedfingers"] = ("🤞", "crossed_fingers"),
    };

    // Unicode 那種：Teams 的名稱跟 Slack 不一樣的才列；沒列的就拿 Teams 名稱試（Slack 不認得就不按）
    private static readonly Dictionary<string, string> SlackByCodepoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["2705"] = "white_check_mark",
        ["1f197"] = "ok",
    };

    /// <summary>顯示用：對得到就是 Unicode（含膚色），對不到用原始代碼 <c>(key)</c>。</summary>
    public static string Glyph(string key)
    {
        var (baseKey, tone) = SplitTone(key);
        if (Named.TryGetValue(baseKey, out var named))
            return named.Glyph + ToneModifier(tone);
        if (CodepointRegex().Match(baseKey) is { Success: true } m)
            return string.Concat(m.Groups["hex"].Value.Split('-').Select(h => char.ConvertFromUtf32(int.Parse(h, NumberStyles.HexNumber)))) + ToneModifier(tone);
        return $"({key})";
    }

    /// <summary>Slack 原生表情名稱（<c>reactions.add</c> 用，膚色是 <c>::skin-tone-N</c>）；對不到 null。</summary>
    public static string? SlackName(string key)
    {
        var (baseKey, tone) = SplitTone(key);
        string? name = null;
        if (Named.TryGetValue(baseKey, out var named))
            name = named.Slack;
        else if (CodepointRegex().Match(baseKey) is { Success: true } m)
            name = SlackByCodepoints.GetValueOrDefault(m.Groups["hex"].Value) ?? m.Groups["name"].Value.ToLowerInvariant();
        // Teams tone1（最淺）～tone5 對 Slack 的 skin-tone-2～6
        return name is null ? null : tone is { } t ? $"{name}::skin-tone-{t + 1}" : name;
    }

    private static (string BaseKey, int? Tone) SplitTone(string key) =>
        ToneRegex().Match(key) is { Success: true } m ? (m.Groups["base"].Value, int.Parse(m.Groups["tone"].Value)) : (key, null);

    // 膚色修飾字元 U+1F3FB（最淺）～U+1F3FF
    private static string ToneModifier(int? tone) => tone is { } t ? char.ConvertFromUtf32(0x1F3FB + t - 1) : "";

    [GeneratedRegex("^(?<base>.+)-tone(?<tone>[1-5])$", RegexOptions.IgnoreCase)]
    private static partial Regex ToneRegex();

    [GeneratedRegex("^(?<hex>[0-9a-f]{4,6}(?:-[0-9a-f]{4,6})*)_(?<name>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CodepointRegex();
}
