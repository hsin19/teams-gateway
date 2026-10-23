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

    // 實際資料：edittime / deletetime 大多是數字（毫秒時間戳），偶爾是字串，兩種都要認得
    [Theory]
    [InlineData("""{"id":"100","version":300,"properties":{"edittime":1792656539873}}""", true, false)]
    [InlineData("""{"id":"100","version":"300","properties":{"edittime":"1792656539873"}}""", true, false)]
    [InlineData("""{"id":"100","version":300,"content":"","properties":{"deletetime":1792656539873}}""", false, true)]
    // 有人按表情：version 往上跳，但沒有 edittime——不是編輯
    [InlineData("""{"id":"100","version":200,"properties":{"emotions":[{"key":"1f440_eyes","users":[]}]}}""", false, false)]
    [InlineData("""{"id":"100","version":100}""", false, false)]
    public void ParseMessage_detects_edits_and_deletes_by_timestamps(string json, bool edited, bool deleted)
    {
        var msg = ChatsvcClient.ParseMessage(JsonDocument.Parse(json).RootElement, "19:abc@thread.v2")!;
        Assert.Equal((edited, deleted), (msg.Edited, msg.Deleted));
    }

    // emotions 是陣列，偶爾是字串化的 JSON；沒人按的表情不算
    [Theory]
    [InlineData("""{"properties":{"emotions":[{"key":"heart","users":[{"mri":"8:a","time":1792640000000},{"mri":"8:b","time":"1792640000500"}]},{"key":"like","users":[]}]}}""")]
    [InlineData("""{"properties":{"emotions":"[{\"key\":\"heart\",\"users\":[{\"mri\":\"8:a\",\"time\":1792640000000},{\"mri\":\"8:b\",\"time\":1792640000500}]}]"}}""")]
    public void ParseReactions_reads_who_reacted_with_what(string json)
    {
        var reactions = ChatsvcClient.ParseReactions(json);

        var heart = Assert.Single(reactions);
        Assert.Equal("heart", heart.Key);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792640000500), heart.Value["8:b"]);
        Assert.Equal(["8:a", "8:b"], heart.Value.Keys.Order());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"properties":{}}""")]
    [InlineData("not json")]
    public void ParseReactions_empty_without_emotions(string json) => Assert.Empty(ChatsvcClient.ParseReactions(json));

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

    // 游標會帶著 skypetoken 原樣打出去：只能是同一個對話、Teams / Skype 主機的 messages 端點
    [Theory]
    [InlineData("https://teams.live.com/api/chatsvc/consumer/v1/users/ME/conversations/19:abc@thread.v2/messages?startTime=1&syncState=x", true)]
    [InlineData("https://teams.live.com/api/chatsvc/consumer/v1/users/ME/conversations/19%3Aabc%40thread.v2/messages?syncState=x", true)]
    [InlineData("https://apac.ng.msg.teams.microsoft.com/v1/users/ME/conversations/19:abc@thread.v2/messages?syncState=x", true)]
    [InlineData("https://attacker.example/api/chatsvc/consumer/v1/users/ME/conversations/19:abc@thread.v2/messages", false)]
    [InlineData("https://teams.live.com.attacker.example/api/chatsvc/consumer/v1/users/ME/conversations/19:abc@thread.v2/messages", false)]
    [InlineData("https://user@teams.live.com/api/chatsvc/consumer/v1/users/ME/conversations/19:abc@thread.v2/messages", false)]
    [InlineData("https://teams.live.com:8443/api/chatsvc/consumer/v1/users/ME/conversations/19:abc@thread.v2/messages", false)]
    [InlineData("http://teams.live.com/api/chatsvc/consumer/v1/users/ME/conversations/19:abc@thread.v2/messages", false)]
    [InlineData("https://teams.live.com/api/chatsvc/consumer/v1/users/ME/conversations/19:other@thread.v2/messages", false)]
    [InlineData("https://teams.live.com/api/chatsvc/consumer/v1/users/ME/properties", false)]
    [InlineData("/api/chatsvc/consumer/v1/users/ME/conversations/19:abc@thread.v2/messages", false)]
    public void IsCursorFor_only_accepts_same_conversation_on_teams_hosts(string cursor, bool expected) =>
        Assert.Equal(expected, ChatsvcClient.IsCursorFor("teams.live.com", "19:abc@thread.v2", cursor));

    [Fact]
    public void MatchByName_prefers_prefix_then_contains_case_insensitive()
    {
        var chats = new List<ConversationDto>
        {
            new("19:a", "General Chat", "Chat", null),
            new("19:b", "Other Online", "Chat", null),
            new("19:c", "my team backup", "Chat", null),
        };

        Assert.Equal(["19:a"], ChatsvcClient.MatchByName(chats, "general chat").Select(c => c.Id));  // 前綴優先
        Assert.Equal(["19:c"], ChatsvcClient.MatchByName(chats, "backup").Select(c => c.Id));      // 退包含
        Assert.Empty(ChatsvcClient.MatchByName(chats, "nope"));
    }

    // 發話不能撤回：「dev」要對到叫 dev 的群，而不是第一個 dev 開頭的
    [Fact]
    public void MatchByName_exact_beats_prefix()
    {
        var chats = new List<ConversationDto>
        {
            new("19:ops", "devops-alerts", "Chat", null),
            new("19:dev", "Dev", "Chat", null),
        };

        Assert.Equal(["19:dev"], ChatsvcClient.MatchByName(chats, "dev").Select(c => c.Id));
    }

    // 同一層多個符合 = 模稜兩可，全部回傳讓呼叫端報錯，不能默默挑第一個
    [Fact]
    public void MatchByName_returns_all_ambiguous_hits()
    {
        var chats = new List<ConversationDto>
        {
            new("19:ops", "devops-alerts", "Chat", null),
            new("19:fe", "dev-frontend", "Chat", null),
            new("19:x", "random", "Chat", null),
        };

        var hits = ChatsvcClient.MatchByName(chats, "dev");

        Assert.Equal(["19:ops", "19:fe"], hits.Select(c => c.Id));
        Assert.Contains("devops-alerts", ChatsvcClient.Ambiguous("dev", hits));
        Assert.Contains("dev-frontend", ChatsvcClient.Ambiguous("dev", hits));
    }
}
