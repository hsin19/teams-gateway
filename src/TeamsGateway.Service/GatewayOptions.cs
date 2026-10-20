namespace TeamsGateway.Service;

/// <summary>設定區段 <c>Gateway</c>。</summary>
public sealed class GatewayOptions
{
    /// <summary>發話時掛的顯示名稱。</summary>
    public string DisplayName { get; set; } = "teams-gateway";

    /// <summary><c>/admin</c> 端點的 token；沒設定則 admin 端點停用。</summary>
    public string? AdminToken { get; set; }

    /// <summary>SQLite 與 DataProtection key 的存放目錄；預設 content root 下的 <c>.data</c>。</summary>
    public string? DataDir { get; set; }
}
