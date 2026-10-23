namespace TeamsGateway.Gateway;

/// <summary>設定區段 <c>Gateway</c>。</summary>
public sealed class GatewayOptions
{
    /// <summary>發話時掛的顯示名稱。</summary>
    public string DisplayName { get; set; } = "teams-gateway";

    /// <summary><c>/admin</c> 端點的 token；沒設定則 admin 端點停用。</summary>
    public string? AdminToken { get; set; }

    /// <summary>SQLite 與 DataProtection key 的存放目錄；預設 content root 下的 <c>.data</c>。</summary>
    public string? DataDir { get; set; }

    /// <summary>資料庫（ADR 0008）：<c>Gateway:Database:Provider</c>。啟動註冊服務時就讀，改了要重啟。</summary>
    public DatabaseSettings Database { get; set; } = new();

    /// <summary>訂閱 / 啟動時回補幾天的歷史訊息（給 Elasticsearch 歷史搜尋留料）。</summary>
    public int BackfillDays { get; set; } = 7;

    /// <summary>慢更新：每隔幾分鐘重掃訂閱對話，補上漏接的編輯 / 刪除。0 = 關閉。</summary>
    public int ReconcileMinutes { get; set; } = 15;

    /// <summary>慢更新每次回看幾小時內的訊息。</summary>
    public int ReconcileWindowHours { get; set; } = 24;

    /// <summary>輪詢保險：每隔幾秒抓一次訂閱對話最近的訊息，補上 Trouter 漏推的新訊息。0 = 關閉。</summary>
    public int PollSeconds { get; set; } = 20;

    /// <summary>
    /// 內部事件 log 至少留幾天：超過的事件等所有 sink 都送過才清；SSE 斷線比這還久的訂閱者重連時就從現在開始收。
    /// webhook 落後超過這麼久還送不出去就停用。
    /// </summary>
    public int EventRetentionDays { get; set; } = 7;

    /// <summary>
    /// 事件出口（ADR 0006）：<c>Gateway:Sinks:&lt;名稱&gt;:Enabled</c>，目前只有 <c>Webhook</c>，沒設 = 開。
    /// 關掉的 sink 不跑、也不擋 pruner 清舊事件。啟動註冊服務時就讀，改了要重啟。
    /// </summary>
    public Dictionary<string, SinkSettings> Sinks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSinkEnabled(string name) => !Sinks.TryGetValue(name, out var sink) || sink.Enabled;

    /// <summary>SSE 閒置時每隔幾秒送一次心跳（順便確認 API key 沒被撤銷）；比中間 proxy / NAT 的閒置逾時短才保得住連線。</summary>
    public int SseHeartbeatSeconds { get; set; } = 20;

    /// <summary>
    /// webhook 可不可以打 loopback / 私有網段（訂閱者跟 gateway 在同一台機器、同網段或同一個 docker 網路時要開）。
    /// link-local（雲端 metadata）不管怎樣都不行。
    /// </summary>
    public bool WebhookAllowPrivateNetworks { get; set; }
}

public sealed class SinkSettings
{
    public bool Enabled { get; set; } = true;
}

public sealed class DatabaseSettings
{
    /// <summary><c>Sqlite</c>（預設，本機開發）或 <c>Postgres</c>（要給 <c>ConnectionStrings:gateway</c>）。</summary>
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;
}

public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}
