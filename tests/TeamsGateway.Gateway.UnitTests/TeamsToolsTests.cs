using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Mcp;

namespace TeamsGateway.Gateway.UnitTests;

public class TeamsToolsTests
{
    private static readonly AttachmentDto GoodMorning = new("good-morning.jpg", "jpg", "https://us-api.asm.skype.com/v1/objects/0-b/views/imgo", "https://1drv.ms/i/c/x/abc");
    private static readonly AttachmentDto Recipe = new("recipe.pdf", "pdf", "", "https://1drv.ms/b/c/x/def");

    private static MessageDto Msg(string content, params AttachmentDto[] attachments) =>
        new("1767310200000", "19:abc@thread.v2", "8:live:grandma", "Grandma", "RichText/Html", content, 1, DateTimeOffset.UnixEpoch, 1, Attachments: attachments);

    private static Task<ContentBlock> FakeFetch(string url, int n) =>
        Task.FromResult<ContentBlock>(new TextContentBlock { Text = $"fetched {n}: {url}" });

    // 圖片（內嵌 + 圖片附件）照順序抓；同一則的檔案下載還沒做，在說明裡列名字，圖片照樣回
    [Fact]
    public async Task Returns_images_and_names_files_not_downloaded()
    {
        var msg = Msg("""<p><img src="https://us-api.asm.skype.com/v1/objects/0-a/views/imgo"></p>""", GoodMorning, Recipe);

        var result = await TeamsTools.AttachmentsAsync(msg, FakeFetch);

        Assert.Equal(
            [
                "2 image(s) in message 1767310200000; files not downloadable yet: recipe.pdf",
                "fetched 1: https://us-api.asm.skype.com/v1/objects/0-a/views/imgo",
                "fetched 2: https://us-api.asm.skype.com/v1/objects/0-b/views/imgo",
            ],
            result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    // 只有檔案：整個回錯誤（下載還沒做），agent 才知道拿不到
    [Fact]
    public async Task Files_only_is_not_implemented()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => TeamsTools.AttachmentsAsync(Msg("", Recipe), FakeFetch));

        Assert.Contains("isn't implemented", ex.Message);
        Assert.Contains("recipe.pdf", ex.Message);
    }

    [Fact]
    public async Task Message_without_attachments_is_an_error() =>
        await Assert.ThrowsAsync<McpException>(() => TeamsTools.AttachmentsAsync(Msg("<p>good morning</p>"), FakeFetch));
}
