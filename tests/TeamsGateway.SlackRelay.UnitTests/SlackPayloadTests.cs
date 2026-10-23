using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>
/// Slack 請求從匿名物件換成具名 record + source-gen：送出去的 JSON 要跟原本一樣（欄位順序不論）。
/// <see cref="Previous"/> 是原本 SlackClient 的序列化設定，匿名物件照抄原本的寫法。
/// </summary>
public class SlackPayloadTests
{
    private static readonly JsonSerializerOptions Previous = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static void AssertSameJson(object previous, string current) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(previous, Previous)), JsonNode.Parse(current)),
            $"previous: {JsonSerializer.Serialize(previous, Previous)}\ncurrent:  {current}");

    private static string Current(SlackMessageRequest request) => JsonSerializer.Serialize(request, SlackJsonContext.Default.SlackMessageRequest);

    [Fact]
    public void Post_with_metadata_and_images()
    {
        var blocks = SlackClient.ImageBlocks("*Me*: hi", ["F1"]);
        var previousBlocks = new object[]
        {
            new { type = "section", text = new { type = "mrkdwn", text = "*Me*: hi" } },
            new { type = "image", slack_file = new { id = "F1" }, alt_text = "image" },
        };

        AssertSameJson(
            new { channel = "C1", text = "*Me*: hi", blocks = previousBlocks, metadata = new { event_type = "teams_relay", event_payload = new { teams_id = "1792640000000", version = (long?)1792640000000 } } },
            Current(new SlackMessageRequest("C1", "*Me*: hi", blocks, Metadata: new SlackMetadata("teams_relay", new SlackRelayPayload("1792640000000", 1792640000000)))));
    }

    [Fact]
    public void Post_without_images_or_version_omits_them()
    {
        object[]? noBlocks = null;
        AssertSameJson(
            new { channel = "C1", text = "hi", blocks = noBlocks, metadata = new { event_type = "teams_relay", event_payload = new { teams_id = "1", version = (long?)null } } },
            Current(new SlackMessageRequest("C1", "hi", null, Metadata: new SlackMetadata("teams_relay", new SlackRelayPayload("1", null)))));
    }

    // chat.update 沒圖要明確送空 blocks，Slack 才會清掉舊圖
    [Fact]
    public void Update_clears_blocks_with_empty_array() =>
        AssertSameJson(
            new { channel = "C1", ts = "111.222", text = "edited", blocks = Array.Empty<object>(), metadata = new { event_type = "teams_relay", event_payload = new { teams_id = "1", version = (long?)2 } } },
            Current(new SlackMessageRequest("C1", "edited", [], Ts: "111.222", Metadata: new SlackMetadata("teams_relay", new SlackRelayPayload("1", 2)))));

    [Fact]
    public void Thread_reply_uses_thread_ts()
    {
        object[]? noBlocks = null;
        AssertSameJson(
            new { channel = "C1", text = "_Original_", blocks = noBlocks, thread_ts = "111.222" },
            Current(new SlackMessageRequest("C1", "_Original_", null, ThreadTs: "111.222")));
    }

    [Fact]
    public void Join_and_uploaded_files()
    {
        AssertSameJson(new { channel = "C1" }, JsonSerializer.Serialize(new JoinRequest("C1"), SlackJsonContext.Default.JoinRequest));
        // 原本這個用的是預設設定（非 Web），欄位名剛好都是小寫
        Assert.Equal(
            JsonSerializer.Serialize(new[] { new { id = "F1", title = "teams-image.png" } }),
            JsonSerializer.Serialize([new UploadedFile("F1", "teams-image.png")], SlackJsonContext.Default.UploadedFileArray));
    }

    [Fact]
    public void Reads_snake_case_history_response()
    {
        const string json = """
            {"ok":true,"messages":[{"ts":"111.222","text":"*Me*: hi","metadata":{"event_type":"teams_relay","event_payload":{"teams_id":"1792640000000","version":1792640480711}}}],
             "response_metadata":{"next_cursor":"abc"}}
            """;

        var history = JsonSerializer.Deserialize(json, SlackJsonContext.Default.HistoryResponse)!;

        Assert.True(history.Ok);
        Assert.Equal("abc", history.ResponseMetadata?.NextCursor);
        var m = Assert.Single(history.Messages!);
        Assert.Equal(("111.222", "teams_relay", "1792640000000", 1792640480711L),
            (m.Ts, m.Metadata?.EventType, m.Metadata?.EventPayload?.TeamsId, m.Metadata?.EventPayload?.Version));
    }

    [Fact]
    public void Reads_upload_ticket()
    {
        var ticket = JsonSerializer.Deserialize("""{"ok":true,"upload_url":"https://files.slack.com/x","file_id":"F1"}""", SlackJsonContext.Default.UploadTicketResponse)!;
        Assert.Equal((true, "https://files.slack.com/x", "F1"), (ticket.Ok, ticket.UploadUrl, ticket.FileId));
    }
}
