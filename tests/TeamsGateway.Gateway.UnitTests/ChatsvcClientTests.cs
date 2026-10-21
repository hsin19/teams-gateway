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
