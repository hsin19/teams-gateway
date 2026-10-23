namespace TeamsGateway.Gateway.Data;

/// <summary>Teams 對話（群組 / 1:1）：gateway 的訂閱清單，加上每個對話的同步進度（<see cref="SyncedThrough"/>）。</summary>
public class Conversation
{
    /// <summary>Teams thread id，例如 <c>19:...@thread.v2</c>。</summary>
    public required string Id { get; set; }
    public string? Topic { get; set; }
    public string? ThreadType { get; set; }
    /// <summary>最後一則發出事件的新訊息 id，只給 <c>/admin/subscriptions</c> 顯示；補收看的是 <see cref="SyncedThrough"/>，不是它。</summary>
    public string? LastMessageId { get; set; }
    /// <summary>gateway 是否要收這個對話的推播。</summary>
    public bool Subscribed { get; set; }
    /// <summary>
    /// 已完整同步到的時間（這之前的訊息都入庫了）。只有回補 / 輪詢這種「整段抓」的才推進；
    /// Trouter 推來的單則不算，所以重啟 / 睡眠後從這裡補缺口，不會被先到的新訊息跳過。
    /// </summary>
    public DateTimeOffset? SyncedThrough { get; set; }
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
    /// <summary>
    /// 訊息版本（新訊息 = Id）；任何變動都會遞增，有人按表情也會。版本變大時要比對看得到的內容，
    /// 有變才是編輯 / 刪除，沒變就只是表情（見 <c>MessageIngestor</c>）。
    /// </summary>
    public long Version { get; set; }
    public bool Edited { get; set; }
    public bool Deleted { get; set; }
    public DateTimeOffset ComposeTime { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    /// <summary>最後更新（收到或被編輯更新）時間。</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>原始 resource JSON，保留給之後更完整的解析或 ES。</summary>
    public required string RawJson { get; set; }

    public Conversation? Conversation { get; set; }
    public ICollection<MessageRevision> Revisions { get; set; } = [];
}

/// <summary>一則訊息被編輯 / 刪除前的舊版本，保留編輯歷史（給稽核與 ES 歷史搜尋）。</summary>
public class MessageRevision
{
    public required string MessageId { get; set; }
    /// <summary>這個被取代掉的版本號。</summary>
    public long Version { get; set; }
    public string? Content { get; set; }
    public required string RawJson { get; set; }
    /// <summary>被新版本取代的時間。</summary>
    public DateTimeOffset SupersededAt { get; set; }

    public MessageEvent? Message { get; set; }
}

/// <summary>
/// 發給訂閱者的每一個事件（新訊息 / 編輯 / 刪除）的流水帳。<see cref="Seq"/> 就是 SSE 的 event id：
/// 斷線重連帶 <c>Last-Event-ID</c> 時照它補收，不受「編輯沿用同一個訊息 id」影響；只存不發的歷史回補不會進來。
/// </summary>
public class PublishedEvent
{
    /// <summary>自增序號（SQLite AUTOINCREMENT，不重用）。</summary>
    public long Seq { get; set; }
    /// <summary>事件種類（SSE 的 event 名稱）：<c>message</c> 或 <c>reaction</c>，見 <c>EventKinds</c>。</summary>
    public string Kind { get; set; } = "message";
    public required string MessageId { get; set; }
    public required string ConversationId { get; set; }
    /// <summary>發出去的 JSON：<c>MessageDto</c> 或 <c>ReactionDto</c>（看 <see cref="Kind"/>）的欄位 + <c>schemaVersion</c> + <c>raw</c>，各傳輸原樣送出。</summary>
    public required string Payload { get; set; }
    /// <summary><see cref="Payload"/> 的格式版本（<c>EventSchema.Version</c>）；放 header 的傳輸（webhook、broker）不用解 payload 就知道。</summary>
    public int SchemaVersion { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
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
    /// <summary>投遞游標：送到（或因對話不符略過）的最後一個事件序號。登記時設成當下最新的序號，只收之後的事件。</summary>
    public long DeliveredSeq { get; set; }

    public ApiKey? ApiKey { get; set; }
}

/// <summary>加密後的機敏資料，值用 ASP.NET DataProtection 加密後再存。目前只有登入 cookie；skypetoken 只放記憶體，重啟後用 cookie 重換。</summary>
public class SecureValue
{
    /// <summary>目前只有 <c>login-cookies</c>。</summary>
    public required string Key { get; set; }
    public required string Protected { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
