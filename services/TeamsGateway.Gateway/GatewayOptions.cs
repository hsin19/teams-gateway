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

    /// <summary>訂閱 / 啟動時回補幾天的歷史訊息（給 Elasticsearch 歷史搜尋留料）。</summary>
    public int BackfillDays { get; set; } = 7;

    /// <summary>慢更新：每隔幾分鐘重掃訂閱對話，補上漏接的編輯 / 刪除。0 = 關閉。</summary>
    public int ReconcileMinutes { get; set; } = 15;

    /// <summary>慢更新每次回看幾小時內的訊息。</summary>
    public int ReconcileWindowHours { get; set; } = 24;

    /// <summary>輪詢保險：每隔幾秒抓一次訂閱對話最近的訊息，補上 Trouter 漏推的新訊息。0 = 關閉。</summary>
    public int PollSeconds { get; set; } = 60;
}
