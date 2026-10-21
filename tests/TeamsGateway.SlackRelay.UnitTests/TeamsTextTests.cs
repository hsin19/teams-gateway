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

    // reply 在上、引用在下：不該出現大塊空行
    [Fact]
    public void Reply_then_quote_has_no_blank_line_gaps()
    {
        const string html =
            "<p>Hi <span itemtype=\"http://schema.skype.com/Mention\">Someone</span>,</p>" +
            "<p>a reply line.</p>" +
            "<blockquote itemscope=\"\" itemtype=\"http://schema.skype.com/Reply\" itemid=\"1\">" +
            "<strong itemprop=\"mri\">Bob</strong><span itemprop=\"time\"></span>" +
            "<p itemprop=\"preview\">quoted text</p></blockquote>";

        var result = TeamsText.ToSlack(html);

        Assert.DoesNotContain("\n\n", result);     // 收斂成單行距，無空行
        Assert.Contains("a reply line.", result);
        Assert.Contains("> ", result);             // 引用
        Assert.Contains("quoted text", result);
    }

    // 引用在上、reply 在下：轉出來要以 "> " 開頭（Format 才會另起一行渲染成 blockquote）
    [Fact]
    public void Quote_then_reply_starts_with_quote()
    {
        const string html =
            "<blockquote itemscope=\"\" itemtype=\"http://schema.skype.com/Reply\" itemid=\"1\">" +
            "<strong itemprop=\"mri\">Carol</strong><span itemprop=\"time\"></span>" +
            "<p itemprop=\"preview\">earlier msg</p></blockquote>" +
            "<p>my response.</p>";

        var result = TeamsText.ToSlack(html);

        Assert.StartsWith("> ", result);
        Assert.Contains("earlier msg", result);
        Assert.Contains("my response.", result);
        Assert.DoesNotContain("\n\n", result);
    }
}
