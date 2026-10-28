using ModelContextProtocol;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Mcp;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

public class McpTextTests
{
    private static readonly TimeZoneInfo Taipei = McpText.ParseZone("+08:00");
    private static readonly DateTimeOffset At = new(2026, 1, 1, 23, 30, 0, TimeSpan.Zero); // +08:00 的早上 7:30

    private static MessageDto Msg(string content, string type = "RichText/Html", bool edited = false, bool deleted = false,
        IReadOnlyList<AttachmentDto>? attachments = null, DateTimeOffset? at = null) =>
        new("1767310200000", "19:abc@thread.v2", "8:live:grandma", "Grandma", type, content, 1, at ?? At, 1767310200000, edited, deleted, attachments);

    // 回覆引用：作者用 MRI 查到的現在名字（HTML 裡的可能是舊的），引用內容 decode 完放引號裡，接著是這則自己的內容
    [Fact]
    public void Line_renders_reply_quote_then_own_text()
    {
        var content = """
            <blockquote itemscope="" itemtype="http://schema.skype.com/Reply" itemid="1767306600000">
            <strong itemprop="mri" itemid="8:live:uncle">Old Name</strong><span itemprop="time" itemid="1767306600000"></span>
            <p itemprop="preview">Don&#39;t forget your umbrella &amp; jacket</p>
            </blockquote>
            <p><strong>Thanks for the reminder!</strong></p>
            """;

        var line = McpText.Line(Msg(content), Taipei, mri => mri == "8:live:uncle" ? "Uncle Bob" : null);

        Assert.Equal("2026-01-02 07:30:00 Grandma: [re Uncle Bob: \"Don't forget your umbrella & jacket\"] Thanks for the reminder!", line);
    }

    // 查不到名字就用 HTML 裡的；引用太長截斷
    [Fact]
    public void Long_quote_is_truncated_and_falls_back_to_html_author()
    {
        var preview = new string('x', 80);
        var content = $"""<blockquote itemtype="http://schema.skype.com/Reply" itemid="1"><strong itemprop="mri" itemid="8:live:mei">Mei</strong><p itemprop="preview">{preview}</p></blockquote><p>ok</p>""";

        var body = McpText.Body(Msg(content));

        Assert.Equal($"[re Mei: \"{new string('x', 60)}…\"] ok", body);
    }

    // 段落 → 換行，後續行縮排兩格；@提及、表情符號圖的 alt、HTML entity 都轉成看得懂的字
    [Fact]
    public void Multiline_text_with_mentions_and_emoji_is_indented()
    {
        var content = """
            <p>Good morning <span itemscope="" itemtype="http://schema.skype.com/Mention" itemid="0">Ming</span>&nbsp;have you eaten?</p>
            <p>Stay happy &amp; healthy <img itemscope="" itemtype="http://schema.skype.com/Emoji" alt="🌸" src="https://statics.teams.cdn.live.net/x/flower.png"></p>
            """;

        var line = McpText.Line(Msg(content), Taipei);

        Assert.Equal("2026-01-02 07:30:00 Grandma: Good morning @Ming have you eaten?\n  Stay happy & healthy 🌸", line);
    }

    // 圖片：自己的 Teams 媒體圖 + 圖片附件算進數量（引用裡的、非 Teams 媒體的不算），其他附件逐一列檔名
    [Fact]
    public void Images_and_files_become_markers_with_message_id()
    {
        var content = """
            <blockquote itemtype="http://schema.skype.com/Reply" itemid="1"><p itemprop="preview"><img src="https://us-api.asm.skype.com/v1/objects/0-quoted/views/imgo"></p></blockquote>
            <p><img src="https://us-api.asm.skype.com/v1/objects/0-a/views/imgo" itemtype="http://schema.skype.com/AMSImage"><img src="https://example.com/x.png"></p>
            <p>Good morning pictures for everyone</p>
            """;
        AttachmentDto[] attachments =
        [
            new("good-morning.jpg", "jpg", "https://us-api.asm.skype.com/v1/objects/0-b/views/imgo", "https://1drv.ms/i/c/x/abc"),
            new("recipe.pdf", "pdf", "", "https://1drv.ms/b/c/x/def"), // 非圖片的預覽圖是空字串
        ];
        var msg = Msg(content, attachments: attachments);

        Assert.Equal(
            ["https://us-api.asm.skype.com/v1/objects/0-a/views/imgo", "https://us-api.asm.skype.com/v1/objects/0-b/views/imgo"],
            McpText.ImageUrls(msg));
        // 引用裡只有圖、沒有作者也沒有字：不留空的 [re]
        Assert.Equal("[2 images #1767310200000] [file: recipe.pdf #1767310200000] Good morning pictures for everyone", McpText.Body(msg));
    }

    // 只附檔、沒有文字
    [Fact]
    public void Attachment_only_message_has_just_the_marker()
    {
        var msg = Msg("", attachments: [new("trip-plan.pdf", "pdf", "", "https://1drv.ms/b/c/x/def")]);

        Assert.Equal("2026-01-02 07:30:00 Grandma: [file: trip-plan.pdf #1767310200000]", McpText.Line(msg, Taipei));
    }

    [Fact]
    public void Edited_and_deleted_are_flagged()
    {
        Assert.Equal("2026-01-02 07:30:00 Grandma (edited): fixed", McpText.Line(Msg("<p>fixed</p>", edited: true), Taipei));
        Assert.Equal("2026-01-02 07:30:00 Grandma: (deleted)", McpText.Line(Msg("", deleted: true), Taipei));
    }

    // 純文字訊息不是 HTML：角括號原樣保留
    [Fact]
    public void Plain_text_message_is_not_parsed_as_html()
    {
        Assert.Equal("love you all <3", McpText.Body(Msg("love you all <3", type: "Text")));
    }

    // 標頭寫對話與時區；範圍內還有更舊的時，最後一行給下一次的 until（帶毫秒，同一秒的不會漏）
    [Fact]
    public void Transcript_has_header_and_more_cursor()
    {
        var older = Msg("<p>morning</p>", at: new DateTimeOffset(2026, 1, 1, 22, 0, 0, 123, TimeSpan.Zero));
        var window = new MessageWindow([older, Msg("<p>good morning everyone</p>")], More: true);

        var text = McpText.Transcript("19:abc@thread.v2", window, Taipei);

        Assert.Equal(
            """
            # 19:abc@thread.v2 | 2 messages | times in UTC+08:00
            2026-01-02 06:00:00 Grandma: morning
            2026-01-02 07:30:00 Grandma: good morning everyone
            # more: until=2026-01-02T06:00:00.123+08:00

            """.ReplaceLineEndings("\n"),
            text);
    }

    [Fact]
    public void Transcript_without_more_has_no_cursor()
    {
        var text = McpText.Transcript("19:abc@thread.v2", new MessageWindow([Msg("<p>good morning</p>")], More: false), TimeZoneInfo.Utc);

        Assert.Equal("# 19:abc@thread.v2 | 1 messages | times in UTC\n2026-01-01 23:30:00 Grandma: good morning\n", text);
    }

    [Fact]
    public void Chats_are_listed_by_latest_activity()
    {
        ConversationDto[] chats =
        [
            new("19:classmates@thread.v2", "Class Reunion", "Chat", At.AddDays(-1)),
            new("19:family@thread.v2", "Family", "Chat", At),
            new("8:live:someone", null, null, null),
        ];

        Assert.Equal(
            "# 3 chats | times in UTC+08:00\n"
            + "19:family@thread.v2 | Family | last 2026-01-02 07:30:00\n"
            + "19:classmates@thread.v2 | Class Reunion | last 2026-01-01 07:30:00\n"
            + "8:live:someone | (no topic) | last -\n",
            McpText.Chats(chats, Taipei, limit: 30));
    }

    // 帳號裡多的是好幾年沒動的對話：只留最近有動靜的 limit 個，標頭寫總數、最後一行提示還有更舊的
    [Fact]
    public void Chats_keep_only_the_most_recent_up_to_limit()
    {
        ConversationDto[] chats =
        [
            new("19:old@thread.skype", null, "Chat", At.AddYears(-8)),
            new("19:family@thread.v2", "Family", "Chat", At),
            new("19:empty@thread.v2", null, null, null),
            new("19:classmates@thread.v2", "Class Reunion", "Chat", At.AddDays(-1)),
        ];

        Assert.Equal(
            "# 2 of 4 chats | times in UTC+08:00\n"
            + "19:family@thread.v2 | Family | last 2026-01-02 07:30:00\n"
            + "19:classmates@thread.v2 | Class Reunion | last 2026-01-01 07:30:00\n"
            + "# more: 2 older chats not shown; raise limit or filter with query\n",
            McpText.Chats(chats, Taipei, limit: 2));
    }

    // 時區：offset 幾種寫法都收；查不到的回 McpException（訊息才會傳到 agent）
    [Theory]
    [InlineData("+08:00", 8 * 60)]
    [InlineData("+0800", 8 * 60)]
    [InlineData("UTC+8", 8 * 60)]
    [InlineData("-05:30", -(5 * 60 + 30))]
    [InlineData("UTC", 0)]
    [InlineData("", 0)]
    public void ParseZone_accepts_offsets(string spec, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), McpText.ParseZone(spec).GetUtcOffset(At));
    }

    [Fact]
    public void ParseZone_rejects_unknown_names() =>
        Assert.Throws<McpException>(() => McpText.ParseZone("Nowhere/Nothing"));

    // 時間參數：帶 offset 的照它的，不帶的當成指定時區的時間
    [Theory]
    [InlineData("2026-01-02T07:30:00+08:00")]
    [InlineData("2026-01-01T23:30:00Z")]
    [InlineData("2026-01-02 07:30")]
    public void ParseTime_reads_iso_or_local_time(string value)
    {
        Assert.Equal(At, McpText.ParseTime(value, Taipei, "since"));
    }

    [Fact]
    public void ParseTime_rejects_garbage_and_allows_empty()
    {
        Assert.Null(McpText.ParseTime(null, Taipei, "since"));
        var ex = Assert.Throws<McpException>(() => McpText.ParseTime("yesterday", Taipei, "since"));
        Assert.Contains("since", ex.Message);
    }
}
