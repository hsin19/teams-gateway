using System.Text.Json;
using TeamsGateway.Contracts;

namespace TeamsGateway.Gateway.UnitTests;

/// <summary>
/// source-gen 的輸出要跟原本 reflection（Web 預設）逐字一樣：已存的事件 payload、外部訂閱者（Python client、webhook）都吃這個格式。
/// </summary>
public class ContractsJsonContextTests
{
    private static readonly JsonSerializerOptions Reflection = new(JsonSerializerDefaults.Web);

    private static readonly MessageDto Message = new(
        "1792640000000", "19:x@thread.v2", "8:live:me", "Me", "RichText/Html", "<p>hi</p>", 7,
        new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero), 1792640480711, Edited: true, Deleted: false,
        [new AttachmentDto("photo.jpg", "jpg", "https://us-api.asm.skype.com/v1/objects/0-a/views/imgo", null)]);

    [Fact]
    public void Message_matches_reflection_output() =>
        Assert.Equal(
            JsonSerializer.Serialize(Message, Reflection),
            JsonSerializer.Serialize(Message, ContractsJsonContext.Default.MessageDto));

    [Fact]
    public void Message_without_attachments_matches_reflection_output()
    {
        var plain = Message with { Attachments = null, FromName = null };
        Assert.Equal(JsonSerializer.Serialize(plain, Reflection), JsonSerializer.Serialize(plain, ContractsJsonContext.Default.MessageDto));
    }

    [Fact]
    public void Page_and_conversations_match_reflection_output()
    {
        var page = new MessagePage([Message], "https://teams.live.com/next");
        IReadOnlyList<ConversationDto> chats = [new("19:x@thread.v2", "Team", "Chat", Message.Time), new("19:y@thread.v2", null, null, null)];

        Assert.Equal(JsonSerializer.Serialize(page, Reflection), JsonSerializer.Serialize(page, ContractsJsonContext.Default.MessagePage));
        Assert.Equal(JsonSerializer.Serialize(chats, Reflection), JsonSerializer.Serialize(chats, ContractsJsonContext.Default.IReadOnlyListConversationDto));
    }

    // /v1/status 原本回匿名物件：改成 contract record 後輸出不能變（relay 與外部訂閱者都在讀）
    [Fact]
    public void Auth_status_matches_previous_anonymous_output()
    {
        var at = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var previous = new { teamsAuth = "failed", lastTokenAt = (DateTimeOffset?)at, lastFailureAt = (DateTimeOffset?)at.AddHours(1), lastError = "cookie 過期", tokenExpiresAt = (DateTimeOffset?)null };

        Assert.Equal(
            JsonSerializer.Serialize(previous, Reflection),
            JsonSerializer.Serialize(new AuthStatusDto("failed", at, at.AddHours(1), "cookie 過期", null), ContractsJsonContext.Default.AuthStatusDto));
    }

    // 舊 payload / 舊 client 可能沒帶 version 之後加的欄位：用建構子的預設值
    [Fact]
    public void Missing_optional_fields_take_defaults()
    {
        const string json = """{"id":"1","conversationId":"19:x@thread.v2","content":"hi","sequenceId":1,"time":"2026-10-21T00:00:00+00:00"}""";

        var msg = JsonSerializer.Deserialize(json, ContractsJsonContext.Default.MessageDto)!;

        Assert.Equal((0L, false, false), (msg.Version, msg.Edited, msg.Deleted));
        Assert.Null(msg.Attachments);
        Assert.Equal(JsonSerializer.Deserialize<MessageDto>(json, Reflection), msg with { Attachments = null });
    }
}
