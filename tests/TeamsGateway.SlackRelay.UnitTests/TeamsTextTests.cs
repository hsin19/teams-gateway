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

        // 標籤都去掉、entity 都 decode（& < > 再跳脫給 Slack）
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain("&nbsp;", result);
        Assert.DoesNotContain("&quot;", result);
        Assert.DoesNotContain("&#128640;", result);

        Assert.Contains("🚀", result);                        // &#128640; → emoji
        Assert.Contains("\"quoted\"", result);                 // &quot; → "
        Assert.Contains("hello &amp; welcome", result);        // decode 成 & 再跳脫，Slack 顯示成 &
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

    // @提及要看得出是在叫人：粗體 + @。內容是合成的假名字
    private const string Mention = "<span itemtype=\"http://schema.skype.com/Mention\" itemscope=\"\" itemid=\"0\">";

    [Theory]
    [InlineData($"<p>{Mention}Alice 🚀</span>&nbsp;{Mention}Bob Lee</span></p>", "*@Alice 🚀* *@Bob Lee*")]
    [InlineData($"<p>Loop {Mention}Carol Wu</span></p>", "Loop *@Carol Wu*")]
    [InlineData($"<p>Hi {Mention}Someone</span>, see this.</p>", "Hi *@Someone*, see this.")]
    [InlineData($"<p>{Mention}A</span>{Mention}B</span></p>", "*@A* *@B*")]                    // 緊貼的兩個提及中間補空格
    [InlineData($"<p>請{Mention}小明</span>看一下</p>", "請 *@小明* 看一下")]                      // 貼著中文字：補空格粗體才會生效
    [InlineData($"<p>{Mention}A &amp; &lt;B&gt;</span></p>", "*@A &amp; &lt;B&gt;*")]           // 名字一樣要跳脫
    [InlineData($"<p>{Mention}&nbsp;</span>hi</p>", "hi")]                                       // 空的提及就拿掉
    public void Mentions_are_bold_with_at_sign(string html, string expected) =>
        Assert.Equal(expected, TeamsText.ToSlack(html));

    // Teams 的 HTML 段落之間是 \r\n（中間夾一段只有圖片的段落）：轉出來不能有空行
    [Fact]
    public void Crlf_between_paragraphs_leaves_no_blank_lines() =>
        Assert.Equal(
            "first line\nsecond line",
            TeamsText.ToSlack("<p>first line</p>\r\n<p><img src=\"https://us-api.asm.skype.com/v1/objects/0-x/views/imgo\"></p>\r\n<p>second line</p>\n"));

    // Teams 那端打的 Slack 語法不能在 Slack 生效：@channel、偽裝連結、使用者提及
    [Theory]
    [InlineData("<p>&lt;!channel&gt; urgent</p>", "&lt;!channel&gt; urgent")]
    [InlineData("<p>&lt;https://evil.example|https://bank.example&gt;</p>", "&lt;https://evil.example|https://bank.example&gt;")]
    [InlineData("<p>hi &lt;@U012345&gt;</p>", "hi &lt;@U012345&gt;")]
    [InlineData("a & b", "a &amp; b")]
    [InlineData("<p>literally &amp;lt;</p>", "literally &amp;lt;")] // 只 decode 一次：打的是字面上的 &lt;
    public void Slack_control_characters_are_escaped(string input, string expected) =>
        Assert.Equal(expected, TeamsText.ToSlack(input));

    [Fact]
    public void Quote_author_and_text_are_escaped()
    {
        const string html =
            "<blockquote itemscope=\"\" itemid=\"1\"><strong itemprop=\"mri\">&lt;!here&gt;</strong>" +
            "<p itemprop=\"preview\">&lt;!channel&gt; &amp; more</p></blockquote><p>reply</p>";

        var result = TeamsText.ToSlack(html);

        Assert.Contains("> *&lt;!here&gt;*", result);
        Assert.Contains("> &lt;!channel&gt; &amp; more", result);
        Assert.DoesNotContain("<!", result);
    }

    private static string QuoteHtml(string id, string author, string preview) =>
        $"<blockquote itemscope=\"\" itemtype=\"http://schema.skype.com/Reply\" itemid=\"{id}\">" +
        $"<strong itemprop=\"mri\">{author}</strong><span itemprop=\"time\" itemid=\"{id}\"></span><p itemprop=\"preview\">{preview}</p></blockquote>";

    [Fact]
    public void Quotes_reads_id_and_trimmed_author()
    {
        Assert.Equal([new TeamsText.QuotedMessage("1792522584402", "Bob")], TeamsText.Quotes(QuoteHtml("1792522584402", "Bob", "x") + "<p>reply</p>"));
        Assert.Equal("Alice", TeamsText.Quotes(ReplyHtml).Single().Author); // 尾端 &nbsp; 要去掉
        Assert.Equal("8:live:someone", TeamsText.Quotes(ReplyHtml).Single().AuthorMri);
    }

    // HTML 裡的作者名不可靠：查得到 MRI 對應的名字就用它，查不到才用 HTML 裡的
    [Fact]
    public void Quote_author_is_resolved_by_mri_when_known()
    {
        Assert.Contains("> *Alice Chen*", TeamsText.ToSlack(ReplyHtml, nameOf: mri => mri == "8:live:someone" ? "Alice Chen" : null));
        Assert.Contains("> *Alice*", TeamsText.ToSlack(ReplyHtml, nameOf: _ => null));
    }

    [Fact]
    public void Quotes_empty_when_not_a_reply()
    {
        Assert.Empty(TeamsText.Quotes("<p>just a message</p>"));
        Assert.Empty(TeamsText.Quotes(null));
    }

    // Teams 一則可以同時引用好幾則：每一則都要讀到，照出現順序
    [Fact]
    public void Quotes_reads_every_quoted_message_in_order()
    {
        var html = QuoteHtml("100", "Alice", "123") + "<p></p>" + QuoteHtml("200", "Bob", "456") + "<p>789</p>";

        Assert.Equal(["100", "200"], TeamsText.Quotes(html).Select(q => q.Id));
    }

    // 已經改用連結的引用不畫；沒連結的（例如沒轉過）照樣畫成 blockquote，不能整個消失
    [Fact]
    public void Only_linked_quotes_are_dropped()
    {
        var html = QuoteHtml("100", "Alice", "123") + "<p></p>" + QuoteHtml("200", "Bob", "456") + "<p>789</p>";

        Assert.Equal("> *Bob*\n> 456\n789", TeamsText.ToSlack(html, new HashSet<string> { "100" }));
    }

    [Fact]
    public void Drops_linked_quote()
    {
        const string html =
            "<blockquote itemscope=\"\" itemid=\"1\"><strong itemprop=\"mri\">Bob</strong>" +
            "<p itemprop=\"preview\">quoted</p></blockquote><p>my reply</p>";
        var result = TeamsText.ToSlack(html, new HashSet<string> { "1" });

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
        Assert.Equal("look", TeamsText.ToSlack(html, new HashSet<string> { "1" }));
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
