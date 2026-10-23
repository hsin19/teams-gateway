using System.Text.Json.Serialization;

namespace TeamsGateway.SlackRelay;

// Slack Web API 的請求 / 回應型別。Slack 是 snake_case，由 SlackJsonContext 的命名規則統一轉，不用逐個標 JsonPropertyName。

/// <summary><c>chat.postMessage</c> / <c>chat.update</c> 的 body；null 的欄位不送（例如沒圖就沒有 blocks、新訊息沒有 ts）。</summary>
internal sealed record SlackMessageRequest(
    string Channel,
    string Text,
    IReadOnlyList<SlackBlock>? Blocks = null,
    string? Ts = null,
    string? ThreadTs = null,
    SlackMetadata? Metadata = null);

/// <summary>Block Kit 的 block。只用到 section（mrkdwn 文字）與 image（引用已上傳的 Slack 檔案）兩種，攤平成一個型別。</summary>
internal sealed record SlackBlock(string Type, SlackText? Text = null, SlackFileRef? SlackFile = null, string? AltText = null)
{
    public static SlackBlock Section(string mrkdwn) => new("section", new SlackText("mrkdwn", mrkdwn));

    public static SlackBlock Image(string fileId) => new("image", SlackFile: new SlackFileRef(fileId), AltText: "image");
}

internal sealed record SlackText(string Type, string Text);

internal sealed record SlackFileRef(string Id);

/// <summary>訊息 metadata：轉過去的訊息夾著 Teams id / 版本，重啟時從 Slack 歷史讀回來重建對應。</summary>
internal sealed record SlackMetadata(string? EventType, SlackRelayPayload? EventPayload);

internal sealed record SlackRelayPayload(string? TeamsId, long? Version);

internal sealed record JoinRequest(string Channel);

/// <summary><c>reactions.add</c> / <c>reactions.remove</c>：在某則訊息按 / 收回一個表情。</summary>
internal sealed record ReactionRequest(string Channel, string Timestamp, string Name);

/// <summary><c>files.completeUploadExternal</c> 的 <c>files</c> 欄位（JSON 字串）裡的一項。</summary>
internal sealed record UploadedFile(string Id, string Title);

internal sealed record PostResponse(bool Ok, string? Ts, string? Error);

internal sealed record UploadTicketResponse(bool Ok, string? UploadUrl, string? FileId, string? Error);

internal sealed record PermalinkResponse(bool Ok, string? Permalink, string? Error);

internal sealed record HistoryResponse(bool Ok, HistoryMessage[]? Messages, ResponseMetadata? ResponseMetadata, string? Error);

internal sealed record HistoryMessage(string? Ts, string? Text, SlackMetadata? Metadata);

internal sealed record ResponseMetadata(string? NextCursor);

/// <summary>Slack Web API 的 source-gen context：snake_case、null 不送。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SlackMessageRequest))]
[JsonSerializable(typeof(IReadOnlyList<SlackBlock>))]
[JsonSerializable(typeof(JoinRequest))]
[JsonSerializable(typeof(ReactionRequest))]
[JsonSerializable(typeof(UploadedFile[]))]
[JsonSerializable(typeof(PostResponse))]
[JsonSerializable(typeof(UploadTicketResponse))]
[JsonSerializable(typeof(PermalinkResponse))]
[JsonSerializable(typeof(HistoryResponse))]
internal sealed partial class SlackJsonContext : JsonSerializerContext;
