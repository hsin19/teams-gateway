namespace TeamsGateway.Contracts;

/// <summary>對外的對話資訊。</summary>
public sealed record ConversationDto(string Id, string? Topic, string? ThreadType, DateTimeOffset? LastMessageTime);

/// <summary>
/// 對外的一則訊息。<c>Version</c> 在編輯時遞增（新訊息 = Id）；<c>Edited</c> / <c>Deleted</c> 標記編輯 / 刪除。
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

public sealed record SendMessageResult(string ClientMessageId);
