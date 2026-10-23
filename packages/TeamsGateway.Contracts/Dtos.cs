namespace TeamsGateway.Contracts;

/// <summary>對外的對話資訊。</summary>
public sealed record ConversationDto(string Id, string? Topic, string? ThreadType, DateTimeOffset? LastMessageTime);

/// <summary>
/// 對外的一則訊息。<c>Version</c> 在訊息有任何變動時遞增（新訊息 = Id，有人按表情也會）；內容有沒有改看 <c>Edited</c> / <c>Deleted</c>。
/// <c>Attachments</c> 是用「附加檔案」傳的檔（不在 <c>Content</c> 裡；只附檔時 <c>Content</c> 是空的）。
/// </summary>
public sealed record MessageDto(
    string Id,
    string ConversationId,
    string? From,
    string? FromName,
    string? Type,
    string? Content,
    long SequenceId,
    DateTimeOffset Time,
    long Version = 0,
    bool Edited = false,
    bool Deleted = false,
    IReadOnlyList<AttachmentDto>? Attachments = null);

/// <summary>
/// 訊息附件（個人版 Teams 存在發送者的 OneDrive）。<c>PreviewUrl</c> 是 Teams 產的預覽圖（圖片才有），
/// 跟內嵌圖片一樣要經 <c>/v1/media</c> 代抓；<c>ShareUrl</c> 是 OneDrive 分享連結。
/// </summary>
public sealed record AttachmentDto(string Name, string? Type, string? PreviewUrl, string? ShareUrl);

/// <summary>一頁訊息；<see cref="NextCursor"/> 傳回下一次查詢的游標（往更舊的方向）。</summary>
public sealed record MessagePage(IReadOnlyList<MessageDto> Messages, string? NextCursor);

public sealed record SendMessageRequest(string Text);

/// <summary>SSE 的 <c>event</c> 名稱，也是 webhook 的 <c>X-TeamsGateway-Event</c> header 值。</summary>
public static class EventKinds
{
    /// <summary>新訊息 / 編輯 / 刪除，資料是 <see cref="MessageDto"/>。</summary>
    public const string Message = "message";

    /// <summary>有人按 / 收回表情，資料是 <see cref="ReactionDto"/>。</summary>
    public const string Reaction = "reaction";
}

/// <summary>
/// 有人在一則訊息上按或收回表情。<c>Emoji</c> 是 Teams 的表情代碼，例如 <c>heart</c>、<c>like-tone2</c>（膚色）、
/// <c>1f440_eyes</c>（Unicode 碼位 + 名稱）。<c>UserName</c> 查不到時是 null。
/// <c>Count</c> 是這個表情變動後還有幾個人按著（收回後是 0 = 已經沒人按了）。
/// </summary>
public sealed record ReactionDto(
    string MessageId,
    string ConversationId,
    string Emoji,
    string UserMri,
    string? UserName,
    bool Added,
    DateTimeOffset Time,
    int Count);

/// <summary>API 的錯誤回應：<c>{"error": "..."}</c>。</summary>
public sealed record ApiError(string Error);

/// <summary><c>GET /v1/messages/{id}/revisions</c>：目前版本 + 被取代的舊版本（由舊到新）。</summary>
public sealed record MessageRevisionsDto(MessageVersionDto Current, IReadOnlyList<RevisionDto> Revisions);

public sealed record MessageVersionDto(long Version, string? Content, bool Edited, bool Deleted, DateTimeOffset UpdatedAt);

public sealed record RevisionDto(long Version, string? Content, DateTimeOffset SupersededAt);

/// <summary>登記 webhook。<c>ConversationId</c> 省略 = 所有訂閱中的對話；<c>Secret</c> 有給才簽 HMAC。</summary>
public sealed record CreateWebhookRequest(string TargetUrl, string? ConversationId = null, string? Secret = null);

/// <summary>一個 webhook（secret 不回傳，只回有沒有設）。</summary>
public sealed record WebhookDto(Guid Id, string? ConversationId, string TargetUrl, bool HasSecret, bool Active, DateTimeOffset CreatedAt);

/// <summary>
/// <c>GET /v1/status</c>：gateway 與 Teams 的認證健康。<c>TeamsAuth</c> 是 <c>ok</c> 或 <c>failed</c>；
/// 訂閱者（relay）拿來偵測登入 cookie 失效並告警。
/// </summary>
public sealed record AuthStatusDto(
    string TeamsAuth,
    DateTimeOffset? LastTokenAt,
    DateTimeOffset? LastFailureAt,
    string? LastError,
    DateTimeOffset? TokenExpiresAt);

public sealed record SendMessageResult(string ClientMessageId);
