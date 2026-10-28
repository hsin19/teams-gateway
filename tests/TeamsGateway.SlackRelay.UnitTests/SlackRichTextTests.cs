using System.Text.Json;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>Slack 訊息 → 送到 Teams 的 HTML（ADR 0012）。</summary>
public class SlackRichTextTests
{
    private const string Bot = "UBOT";

    private static string? UserName(string id) => id == "U2" ? "Alice" : null;

    private static string? ChannelName(string id) => id == "C9" ? "general" : null;

    // rich_text 的結構都轉成對應的 HTML；開頭的 @bot 與它後面的空白拿掉，句子中間的 @人換成名字
    [Fact]
    public void Rich_text_becomes_html_without_the_leading_mention()
    {
        var message = Message("""
            [{"type":"rich_text","elements":[
              {"type":"rich_text_section","elements":[
                {"type":"user","user_id":"UBOT"},{"type":"text","text":" hi "},
                {"type":"text","text":"bold","style":{"bold":true}},{"type":"text","text":" "},
                {"type":"text","text":"it","style":{"italic":true}},{"type":"text","text":" "},
                {"type":"text","text":"gone","style":{"strike":true}},{"type":"text","text":" "},
                {"type":"text","text":"x<y","style":{"code":true}},{"type":"text","text":" "},
                {"type":"link","url":"https://example.com?a=1&b=2","text":"site"},{"type":"text","text":" "},
                {"type":"emoji","name":"thumbsup","unicode":"1f44d-1f3fb"},{"type":"text","text":" "},
                {"type":"emoji","name":"teams-relay"},{"type":"text","text":" ask "},
                {"type":"user","user_id":"U2"},{"type":"text","text":" in "},
                {"type":"channel","channel_id":"C9"},{"type":"text","text":"\nline 2"}]},
              {"type":"rich_text_list","style":"ordered","elements":[
                {"type":"rich_text_section","elements":[{"type":"text","text":"one"}]},
                {"type":"rich_text_section","elements":[{"type":"text","text":"two"}]}]},
              {"type":"rich_text_quote","elements":[{"type":"text","text":"quoted"}]},
              {"type":"rich_text_preformatted","elements":[{"type":"text","text":"a\nb"}]}]}]
            """);

        var html = SlackRichText.ToHtml(message, Bot, UserName, ChannelName);

        Assert.Equal(
            "<p>hi <b>bold</b> <i>it</i> <s>gone</s> <code>x&lt;y</code> <a href=\"https://example.com?a=1&amp;b=2\">site</a> 👍🏻 :teams-relay: "
            + "ask @Alice in #general<br>line 2</p><ol><li>one</li><li>two</li></ol><blockquote>quoted</blockquote><pre>a\nb</pre>",
            html);
    }

    // 沒有 blocks（極少數）：退回 mrkdwn 的 text，<…> 的 @、#、連結都轉，Slack 的 entity 還原後再跳脫
    [Fact]
    public void Mrkdwn_text_is_the_fallback_without_blocks()
    {
        var message = new HistoryMessage("1.2", "<@UBOT> hi <https://x.com|site> &amp; <@U2> <#C9|general> <!here>\nbye", null, User: "UME");

        Assert.Equal("<p>hi <a href=\"https://x.com\">site</a> &amp; @Alice #general @here<br>bye</p>",
            SlackRichText.ToHtml(message, Bot, UserName, ChannelName));
    }

    // 只有 @bot、沒有內容：轉出來是空的，不送
    [Fact]
    public void Only_the_mention_is_blank()
    {
        var message = Message("""[{"type":"rich_text","elements":[{"type":"rich_text_section","elements":[{"type":"user","user_id":"UBOT"},{"type":"text","text":" "}]}]}]""");

        Assert.True(SlackRichText.IsBlank(SlackRichText.ToHtml(message, Bot, UserName, ChannelName)));
        Assert.False(SlackRichText.IsBlank("<p>hi</p>"));
    }

    // 開頭是 @bot 才算要發；在句子中間是在討論 bot 本身
    [Theory]
    [InlineData("<@UBOT> hi", true)]
    [InlineData("  <@UBOT|teams-relay> hi", true)]
    [InlineData("hey <@UBOT> look", false)]
    [InlineData("<@UBOT2> hi", false)]
    [InlineData(null, false)]
    public void Only_a_leading_mention_counts(string? text, bool expected) =>
        Assert.Equal(expected, SlackRichText.StartsWithMention(text, Bot));

    // 確認訊息裡的預覽：拿掉 @bot 的原文，每行放進 Slack 的引用
    [Fact]
    public void Preview_quotes_the_text_without_the_mention() =>
        Assert.Equal("> hi *there*\n> second", SlackRichText.Preview("<@UBOT> hi *there*\nsecond", Bot));

    private static HistoryMessage Message(string blocks) =>
        new("1.2", "<@UBOT> …", null, User: "UME", Blocks: JsonDocument.Parse(blocks).RootElement.Clone());
}
