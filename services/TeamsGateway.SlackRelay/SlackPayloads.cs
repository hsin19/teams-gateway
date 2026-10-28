using System.Text.Json;
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

/// <summary>
/// Block Kit 的 block，攤平成一個型別：section（mrkdwn 文字）、image（引用已上傳的 Slack 檔案）、
/// actions（發話確認的按鈕，見 <see cref="SlackButton"/>）。
/// </summary>
internal sealed record SlackBlock(
    string Type, SlackText? Text = null, SlackFileRef? SlackFile = null, string? AltText = null, IReadOnlyList<SlackButton>? Elements = null)
{
    public static SlackBlock Section(string mrkdwn) => new("section", new SlackText("mrkdwn", mrkdwn));

    public static SlackBlock Image(string fileId) => new("image", SlackFile: new SlackFileRef(fileId), AltText: "image");

    public static SlackBlock Actions(params IReadOnlyList<SlackButton> buttons) => new("actions", Elements: buttons);
}

internal sealed record SlackText(string Type, string Text);

/// <summary>按鈕：<c>ActionId</c> 決定按了做什麼，<c>Value</c> 帶著要處理的那則訊息（見 <c>SlackSender</c>）。</summary>
internal sealed record SlackButton(SlackText Text, string ActionId, string Value, string? Style = null, string Type = "button")
{
    public static SlackButton Of(string label, string actionId, string value, string? style = null) =>
        new(new SlackText("plain_text", label), actionId, value, style);
}

internal sealed record SlackFileRef(string Id);

/// <summary>
/// 訊息 metadata：轉過去的訊息（<c>teams_relay</c>）夾著 Teams id / 版本，重啟時從 Slack 歷史讀回來重建對應；
/// 表情留言（<c>teams_reaction</c>）夾著 gateway 的事件序號，重送時認得出貼過了；
/// 人從 Slack 發的那則在 Teams 上被改時貼的「Edited / Deleted in Teams」（<c>teams_edit_note</c>）夾著 Teams id / 版本，重啟後不重貼。
/// </summary>
internal sealed record SlackMetadata(string? EventType, SlackRelayPayload? EventPayload);

/// <summary><c>Chat</c>：那則 Teams 訊息所在的對話 id，從 Slack 回覆時要靠它知道送去哪（一個頻道可能對到多個對話）。</summary>
internal sealed record SlackRelayPayload(string? TeamsId, long? Version, long? Seq = null, string? Chat = null);

internal sealed record JoinRequest(string Channel);

/// <summary><c>reactions.add</c> / <c>reactions.remove</c>：在某則訊息按 / 收回一個表情。</summary>
internal sealed record ReactionRequest(string Channel, string Timestamp, string Name);

/// <summary><c>files.completeUploadExternal</c> 的 <c>files</c> 欄位（JSON 字串）裡的一項。</summary>
internal sealed record UploadedFile(string Id, string Title);

internal sealed record PostResponse(bool Ok, string? Ts, string? Error);

internal sealed record UploadTicketResponse(bool Ok, string? UploadUrl, string? FileId, string? Error);

internal sealed record PermalinkResponse(bool Ok, string? Permalink, string? Error);

internal sealed record HistoryResponse(bool Ok, HistoryMessage[]? Messages, ResponseMetadata? ResponseMetadata, string? Error);

/// <summary>
/// 讀歷史 / 讀單則 / <c>reactions.get</c> 回的訊息。<c>Blocks</c> 是人發的訊息的 rich_text（原樣留著給 <c>SlackRichText</c> 轉）；
/// <c>User</c> 是作者（bot 貼的是 <c>BotId</c>）。
/// </summary>
internal sealed record HistoryMessage(
    string? Ts,
    string? Text,
    SlackMetadata? Metadata,
    string? User = null,
    string? BotId = null,
    string? ThreadTs = null,
    JsonElement? Blocks = null,
    SlackReaction[]? Reactions = null,
    SlackFileInfo[]? Files = null);

internal sealed record SlackReaction(string? Name, string[]? Users, int Count);

internal sealed record SlackFileInfo(string? Name);

internal sealed record ResponseMetadata(string? NextCursor);

/// <summary><c>conversations.info</c>：只用到頻道名稱（啟動時印路由用）。</summary>
internal sealed record ChannelInfoResponse(bool Ok, SlackChannel? Channel, string? Error);

internal sealed record SlackChannel(string? Id, string? Name);

/// <summary><c>auth.test</c>：bot 自己的 user id。</summary>
internal sealed record AuthTestResponse(bool Ok, string? UserId, string? Error);

/// <summary><c>chat.postEphemeral</c>：只有 <c>User</c> 看得到。</summary>
internal sealed record EphemeralRequest(string Channel, string User, string Text, IReadOnlyList<SlackBlock>? Blocks = null, string? ThreadTs = null);

/// <summary>經互動事件的 <c>response_url</c> 改掉或刪掉那則 ephemeral。</summary>
internal sealed record ResponseUrlRequest(string? Text = null, IReadOnlyList<SlackBlock>? Blocks = null, bool? ReplaceOriginal = null, bool? DeleteOriginal = null);

/// <summary><c>reactions.get</c>：那則訊息連同上面的表情。</summary>
internal sealed record ReactionsResponse(bool Ok, HistoryMessage? Message, string? Error);

/// <summary><c>users.info</c>：只用到名字。</summary>
internal sealed record UserInfoResponse(bool Ok, SlackUser? User, string? Error);

internal sealed record SlackUser(string? Name, string? RealName, SlackUserProfile? Profile);

internal sealed record SlackUserProfile(string? DisplayName, string? RealName);

/// <summary><c>apps.connections.open</c>：Socket Mode 的 WebSocket 網址（用一次就失效）。</summary>
internal sealed record ConnectionsOpenResponse(bool Ok, string? Url, string? Error);

/// <summary>
/// Socket Mode 收到的一則：<c>hello</c>、<c>disconnect</c>（<c>Reason</c>），或要 ack 的 <c>events_api</c> / <c>interactive</c>
/// （<c>Payload</c> 分別是 <see cref="EventCallback"/> / <see cref="BlockActionsPayload"/>）。
/// </summary>
internal sealed record SocketEnvelope(string? Type, string? EnvelopeId = null, JsonElement? Payload = null, string? Reason = null, int? RetryAttempt = null);

internal sealed record SocketAck(string EnvelopeId);

internal sealed record EventCallback(string? Type, string? EventId, SlackEvent? Event);

/// <summary>訂閱的事件（目前只有 <c>app_mention</c>）。<c>ThreadTs</c> 有值而且不等於 <c>Ts</c> = thread 裡的回覆。</summary>
internal sealed record SlackEvent(string? Type, string? User, string? Text, string? Ts, string? Channel, string? ThreadTs = null, string? BotId = null);

/// <summary>按鈕事件。<c>Container</c> 是按鈕所在的那則 ephemeral。</summary>
internal sealed record BlockActionsPayload(string? Type, SlackIdRef? User, SlackIdRef? Channel, string? ResponseUrl, BlockAction[]? Actions);

internal sealed record SlackIdRef(string? Id);

internal sealed record BlockAction(string? ActionId, string? Value);

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
[JsonSerializable(typeof(ChannelInfoResponse))]
[JsonSerializable(typeof(AuthTestResponse))]
[JsonSerializable(typeof(EphemeralRequest))]
[JsonSerializable(typeof(ResponseUrlRequest))]
[JsonSerializable(typeof(ReactionsResponse))]
[JsonSerializable(typeof(UserInfoResponse))]
[JsonSerializable(typeof(ConnectionsOpenResponse))]
[JsonSerializable(typeof(SocketEnvelope))]
[JsonSerializable(typeof(SocketAck))]
[JsonSerializable(typeof(EventCallback))]
[JsonSerializable(typeof(BlockActionsPayload))]
internal sealed partial class SlackJsonContext : JsonSerializerContext;
