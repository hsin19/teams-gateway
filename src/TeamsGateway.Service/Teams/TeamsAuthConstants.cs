namespace TeamsGateway.Service.Teams;

/// <summary>
/// 個人版 Teams（teams.live.com）認證用的非官方端點與參數，對應 spike/FINDINGS.md。
/// 這些是逆向網頁版得來的，Microsoft 隨時可能改。
/// </summary>
internal static class TeamsAuthConstants
{
    public const string ClientId = "4b3e8f46-56d3-427f-b1e2-d239b2ea6bca";
    public const string RedirectUri = "https://teams.live.com/v2/authv2";

    public const string Authorize = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";
    public const string Token = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    public const string Authz = "https://teams.live.com/api/auth/v2.0/authz/consumer";

    public const string LoginScope = "https://mtsvc.fl.teams.microsoft.com/teams.mt.readwrite openid profile offline_access";
    public const string AuthScope = "https://auth.fl.teams.microsoft.com/teams.auth.readwrite openid profile offline_access";

    public const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36";

    public const string ClientVersion = "1415/26100816141";

    /// <summary>skypetoken 剩多久以內就提前換。</summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(10);

    public const string CookieSecureKey = "login-cookies";
}
