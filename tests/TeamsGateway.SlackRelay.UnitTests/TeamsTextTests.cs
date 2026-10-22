using TeamsGateway.Contracts;
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
        Assert.Contains("> *Alice*", result);                  // 引用：發話者粗體
    }

    [Theory]
    [InlineData("just plain text", "just plain text")]
    [InlineData("", "")]
    public void Plain_text_passes_through(string input, string expected) =>
        Assert.Equal(expected, TeamsText.ToSlack(input));

    [Fact]
    public void Null_is_safe() => Assert.Equal("", TeamsText.ToSlack(null));

    [Fact]
    public void QuotedMessageId_reads_blockquote_itemid()
    {
        const string html =
            "<blockquote itemscope=\"\" itemtype=\"http://schema.skype.com/Reply\" itemid=\"1792522584402\">" +
            "<strong itemprop=\"mri\">Bob</strong><p itemprop=\"preview\">x</p></blockquote><p>reply</p>";
        Assert.Equal("1792522584402", TeamsText.QuotedMessageId(html));
    }

    [Fact]
    public void QuotedAuthorName_reads_trimmed_author()
    {
        Assert.Equal("Alice", TeamsText.QuotedAuthorName(ReplyHtml)); // 尾端 &nbsp; 要去掉
        Assert.Null(TeamsText.QuotedAuthorName("<p>just a message</p>"));
        Assert.Null(TeamsText.QuotedAuthorName(null));
    }

    [Fact]
    public void QuotedMessageId_null_when_not_a_reply()
    {
        Assert.Null(TeamsText.QuotedMessageId("<p>just a message</p>"));
        Assert.Null(TeamsText.QuotedMessageId(null));
    }

    [Fact]
    public void Drops_quote_when_includeQuote_false()
    {
        const string html =
            "<blockquote itemscope=\"\" itemid=\"1\"><strong itemprop=\"mri\">Bob</strong>" +
            "<p itemprop=\"preview\">quoted</p></blockquote><p>my reply</p>";
        var result = TeamsText.ToSlack(html, includeQuote: false);

        Assert.DoesNotContain(">", result);     // 完全沒有引用
        Assert.DoesNotContain("quoted", result);
        Assert.Equal("my reply", result);
    }

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
        Assert.Contains("> *Bob*", result);        // 引用：發話者粗體
        Assert.Contains("> quoted text", result);
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

        Assert.StartsWith("> *Carol*", result);
        Assert.Contains("> earlier msg", result);
        Assert.Contains("my response.", result);
        Assert.DoesNotContain("\n\n", result);
    }

    // 內嵌圖片：只取這則訊息自己的 Teams 媒體圖，引用裡的圖、表情 CDN 圖不算；圖本身不留文字
    [Fact]
    public void ImageUrls_returns_own_teams_media_only()
    {
        const string html =
            "<blockquote itemscope=\"\" itemid=\"1\"><strong itemprop=\"mri\">Bob</strong>" +
            "<p itemprop=\"preview\"><img src=\"https://us-api.asm.skype.com/v1/objects/0-quoted/views/imgo\"></p></blockquote>" +
            "<p>look<img itemtype=\"http://schema.skype.com/AMSImage\" alt=\"image\" " +
            "src=\"https://us-api.asm.skype.com/v1/objects/0-ea-d4-abc/views/imgo\" width=\"250\"></p>" +
            "<p><img itemtype=\"http://schema.skype.com/Emoji\" src=\"https://statics.teams.cdn.live.net/evergreen-assets/emoji/smile.png\"></p>" +
            "<p><img itemtype=\"http://schema.skype.com/AMSImage\" src=\"https://us-api.asm.skype.com/v1/objects/0-ea-d4-abc/views/imgo\"></p>";

        var urls = TeamsText.ImageUrls(html);

        Assert.Equal(["https://us-api.asm.skype.com/v1/objects/0-ea-d4-abc/views/imgo"], urls); // 重複的只算一次
        Assert.Equal("look", TeamsText.ToSlack(html, includeQuote: false));
    }

    // 附件：圖片類型 + 有預覽圖才當圖片；影片有縮圖也算一般檔案、沒預覽圖的圖也轉不了
    [Theory]
    [InlineData("jpg", "https://us-api.asm.skype.com/v1/objects/0-a/views/imgo", true)]
    [InlineData("PNG", "https://us-api.asm.skype.com/v1/objects/0-b/views/imgo", true)]
    [InlineData("mp4", "https://us-api.asm.skype.com/v1/objects/0-c/views/imgo", false)]
    [InlineData("jpg", null, false)]
    [InlineData("pdf", null, false)]
    public void IsImage_needs_image_type_and_preview(string type, string? previewUrl, bool expected) =>
        Assert.Equal(expected, TeamsText.IsImage(new AttachmentDto("f", type, previewUrl, null)));

    [Fact]
    public void ImageUrls_empty_without_images()
    {
        Assert.Empty(TeamsText.ImageUrls("<p>no pictures</p>"));
        Assert.Empty(TeamsText.ImageUrls(null));
    }
}
