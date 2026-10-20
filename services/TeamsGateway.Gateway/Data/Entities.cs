namespace TeamsGateway.Gateway.Data;

/// <summary>Teams 對話（群組 / 1:1）。也當推播補收的游標來源。</summary>
public class Conversation
{
    /// <summary>Teams thread id，例如 <c>19:...@thread.v2</c>。</summary>
    public required string Id { get; set; }
    public string? Topic { get; set; }
    public string? ThreadType { get; set; }
    /// <summary>收到的最後一則訊息 id；斷線補收時當游標。</summary>
    public string? LastMessageId { get; set; }
    /// <summary>gateway 是否要收這個對話的推播。</summary>
    public bool Subscribed { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<MessageEvent> Messages { get; set; } = [];
}

/// <summary>收到的一則訊息事件。以 Teams 訊息 id 當主鍵，天然去重；也給 SSE / webhook / 之後的 ES sink 消費。</summary>
public class MessageEvent
{
    /// <summary>Teams 訊息 id（遞增的時間戳字串）。</summary>
    public required string Id { get; set; }
    public required string ConversationId { get; set; }
    /// <summary>發話者 MRI，例如 <c>8:live:...</c>。自己發的也會推回來，靠這個分辨。</summary>
    public string? SenderMri { get; set; }
    public string? SenderName { get; set; }
    public string? MessageType { get; set; }
    public string? Content { get; set; }
    public long SequenceId { get; set; }
    public DateTimeOffset ComposeTime { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    /// <summary>原始 resource JSON，保留給之後更完整的解析或 ES。</summary>
    public required string RawJson { get; set; }

    public Conversation? Conversation { get; set; }
}

/// <summary>訂閱者的 API key。只存 hash。</summary>
public class ApiKey
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string KeyHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public ICollection<WebhookSubscription> Webhooks { get; set; } = [];
}

/// <summary>一個 webhook 訂閱：某 API key 對某對話（null = 全部訂閱中的對話）有新訊息時 POST 過去。</summary>
public class WebhookSubscription
{
    public Guid Id { get; set; }
    public Guid ApiKeyId { get; set; }
    public string? ConversationId { get; set; }
    public required string TargetUrl { get; set; }
    /// <summary>HMAC 簽章用的密鑰。</summary>
    public string? Secret { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    public ApiKey? ApiKey { get; set; }
}

/// <summary>加密後的機敏資料（登入 cookie、skypetoken 快取）。值用 ASP.NET DataProtection 加密後再存。</summary>
public class SecureValue
{
    /// <summary>例如 <c>login-cookies</c>、<c>skypetoken</c>。</summary>
    public required string Key { get; set; }
    public required string Protected { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
