using System.Text.Json;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

public class ChatsvcClientTests
{
    [Fact]
    public void ParseMessage_reads_push_resource()
    {
        // Trouter 推播的 resource：sequenceId 是數字、from 是 URL、時間帶 offset
        var resource = JsonDocument.Parse("""
            {
              "id": "1792527319389",
              "content": "hello",
              "from": "https://notifications.skype.net/v1/users/ME/contacts/8:live:testuser",
              "imdisplayname": "teams-gateway",
              "messagetype": "Text",
              "sequenceId": 6,
              "composetime": "2026-10-20T20:15:19.389+00:00",
              "to": "19:abc@thread.v2"
            }
            """).RootElement;

        var msg = ChatsvcClient.ParseMessage(resource, "19:abc@thread.v2");

        Assert.NotNull(msg);
        Assert.Equal("1792527319389", msg!.Id);
        Assert.Equal("8:live:testuser", msg.From);     // 從 URL 取 MRI
        Assert.Equal("teams-gateway", msg.FromName);
        Assert.Equal("Text", msg.Type);
        Assert.Equal(6, msg.SequenceId);              // 數字也要讀得到（曾是 0 的 bug）
        Assert.Equal("hello", msg.Content);
    }

    // 只附檔的訊息：content 空、檔案在 properties.files（字串化的 JSON 陣列），內容是合成的假資料
    [Fact]
    public void ParseMessage_reads_file_attachments()
    {
        var files = JsonSerializer.Serialize(new object[]
        {
            new
            {
                fileName = "photo.jpg", fileType = "jpg",
                fileInfo = new { shareUrl = "https://1drv.ms/i/c/x/abc" },
                filePreview = new { previewUrl = "https://us-api.asm.skype.com/v1/objects/0-ea-d7-abc/views/imgo" },
            },
            new { fileName = "report.pdf", fileType = "pdf", fileInfo = new { shareUrl = "https://1drv.ms/b/c/x/def" } },
        });
        var resource = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "1792641131222",
            content = "",
            messagetype = "RichText/Html",
            properties = new { files },
        })).RootElement;

        var msg = ChatsvcClient.ParseMessage(resource, "19:abc@thread.v2");

        Assert.NotNull(msg?.Attachments);
        Assert.Equal(
            [
                new AttachmentDto("photo.jpg", "jpg", "https://us-api.asm.skype.com/v1/objects/0-ea-d7-abc/views/imgo", "https://1drv.ms/i/c/x/abc"),
                new AttachmentDto("report.pdf", "pdf", null, "https://1drv.ms/b/c/x/def"),
            ],
            msg.Attachments);
        Assert.Equal(msg.Attachments, ChatsvcClient.ParseAttachments(resource.GetRawText())); // 從 DB 的 RawJson 讀回來也一樣
    }

    [Fact]
    public void ParseMessage_without_files_has_no_attachments()
    {
        var resource = JsonDocument.Parse("""{"id":"1","content":"hi","properties":{"files":"[]"}}""").RootElement;
        Assert.Null(ChatsvcClient.ParseMessage(resource, "19:abc@thread.v2")!.Attachments);
    }

    [Fact]
    public void ParseMessage_returns_null_without_id()
    {
        var resource = JsonDocument.Parse("""{"content":"x"}""").RootElement;
        Assert.Null(ChatsvcClient.ParseMessage(resource, "19:abc@thread.v2"));
    }

    [Theory]
    [InlineData("19:abc@thread.v2")]
    [InlineData("19:xyz@thread.skype")]
    [InlineData("something@thread.v2")]
    public void IsThreadId_true_for_ids(string spec) => Assert.True(ChatsvcClient.IsThreadId(spec));

    [Theory]
    [InlineData("General Chat")]
    [InlineData("teams-gateway test")]
    public void IsThreadId_false_for_names(string spec) => Assert.False(ChatsvcClient.IsThreadId(spec));

    [Fact]
    public void MatchByName_prefers_prefix_then_contains_case_insensitive()
    {
        var chats = new List<ConversationDto>
        {
            new("19:a", "General Chat", "Chat", null),
            new("19:b", "Other Online", "Chat", null),
            new("19:c", "my team backup", "Chat", null),
        };

        Assert.Equal("19:a", ChatsvcClient.MatchByName(chats, "general chat")!.Id);  // 前綴優先
        Assert.Equal("19:c", ChatsvcClient.MatchByName(chats, "backup")!.Id);      // 退包含
        Assert.Null(ChatsvcClient.MatchByName(chats, "nope"));
    }
}
