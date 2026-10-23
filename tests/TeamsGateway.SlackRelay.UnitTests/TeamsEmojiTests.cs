using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>Teams 表情代碼 → 顯示字元 / Slack 表情名稱。代碼是照實際資料出現過的。</summary>
public class TeamsEmojiTests
{
    [Theory]
    [InlineData("heart", "❤️", "heart")]
    [InlineData("like", "👍", "+1")]
    [InlineData("LIKE", "👍", "+1")]
    [InlineData("1f440_eyes", "👀", "eyes")]                         // Unicode 那種：Teams 名稱剛好是 Slack 的
    [InlineData("1f4cc_pushpin", "📌", "pushpin")]
    [InlineData("2705_whiteheavycheckmark", "✅", "white_check_mark")] // 名稱不一樣的查表
    [InlineData("1f197_squaredok", "🆗", "ok")]
    [InlineData("fistbump-tone1", "👊🏻", "punch::skin-tone-2")]      // 膚色：Teams tone1 = Slack skin-tone-2
    [InlineData("yes-tone2", "👍🏼", "+1::skin-tone-3")]
    [InlineData("praying-tone1", "🙏🏻", "pray::skin-tone-2")]
    public void Known_codes(string key, string glyph, string slack)
    {
        Assert.Equal(glyph, TeamsEmoji.Glyph(key));
        Assert.Equal(slack, TeamsEmoji.SlackName(key));
    }

    // 對不到：顯示原始代碼、不按原生表情
    [Theory]
    [InlineData("likeWithFaceMSER")]
    [InlineData("somethingnew-tone3")]
    public void Unknown_codes_show_the_key_and_have_no_slack_name(string key)
    {
        Assert.Equal($"({key})", TeamsEmoji.Glyph(key));
        Assert.Null(TeamsEmoji.SlackName(key));
    }
}
