namespace TeamsGateway.Contracts;

/// <summary>對外的對話資訊。</summary>
public sealed record ConversationDto(string Id, string? Topic, string? ThreadType, DateTimeOffset? LastMessageTime);

/// <summary>對外的一則訊息。</summary>
public sealed record MessageDto(
    string Id,
    string ConversationId,
    string? From,
    string? FromName,
    string? Type,
    string? Content,
    long SequenceId,
    DateTimeOffset Time);

/// <summary>一頁訊息；<see cref="NextCursor"/> 傳回下一次查詢的游標（往更舊的方向）。</summary>
public sealed record MessagePage(IReadOnlyList<MessageDto> Messages, string? NextCursor);

public sealed record SendMessageRequest(string Text);

public sealed record SendMessageResult(string ClientMessageId);
