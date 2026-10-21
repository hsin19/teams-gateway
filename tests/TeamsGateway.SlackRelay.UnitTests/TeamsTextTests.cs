using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

public class TeamsTextTests
{
    // 模擬 Teams 的回覆（blockquote）+ RichText/Html，內容是合成的假資料
    private const string ReplyHtml =
        "<div>" +
        "<blockquote itemscope=\"\" itemtype=\"http://schema.skype.com/Reply\" itemid=\"1\">" +
        "<p><b><span itemprop=\"mri\" itemid=\"8:live:someone\">Alice&nbsp;</span></b></p>" +
        "<p itemprop=\"preview\">earlier &quot;quoted&quot; text &#128640;</p>" +
        "</blockquote>" +
        "hello &amp; welcome<br>second line</div>";

    [Fact]
    public void Converts_reply_html_to_readable_slack_text()
    {
        var result = TeamsText.ToSlack(ReplyHtml);

        // 標籤都去掉、entity 都 decode
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain("&nbsp;", result);
        Assert.DoesNotContain("&quot;", result);
        Assert.DoesNotContain("&#128640;", result);
        Assert.DoesNotContain("&amp;", result);

        Assert.Contains("🚀", result);                        // &#128640; → emoji
        Assert.Contains("\"quoted\"", result);                 // &quot; → "
        Assert.Contains("hello & welcome", result);            // &amp; → &
        Assert.Contains("second line", result);                // <br> → 換行後仍在
        Assert.StartsWith("> ", result);                       // blockquote → Slack 引用
        Assert.Contains("Alice", result);                      // 引用的發話者
    }

    [Theory]
    [InlineData("just plain text", "just plain text")]
    [InlineData("", "")]
    public void Plain_text_passes_through(string input, string expected) =>
        Assert.Equal(expected, TeamsText.ToSlack(input));

    [Fact]
    public void Null_is_safe() => Assert.Equal("", TeamsText.ToSlack(null));
}
