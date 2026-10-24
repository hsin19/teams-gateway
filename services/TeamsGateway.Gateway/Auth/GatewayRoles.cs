namespace TeamsGateway.Gateway.Auth;

/// <summary>
/// 角色，也是同名的 authorization policy（ADR 0011）。端點只要求角色，不管身分是哪個 scheme 認證的；
/// 哪種身分拿到哪些角色，看 <see cref="CurrentUser"/>。
/// </summary>
public static class GatewayRoles
{
    /// <summary><c>/admin</c>：建 API key、匯入登入 cookie、管訂閱。</summary>
    public const string Admin = "admin";

    /// <summary><c>/v1</c>、<c>/mcp</c>：讀發訊息、事件串流、webhook。</summary>
    public const string Api = "api";
}

/// <summary>authentication scheme 的名稱。</summary>
public static class AuthSchemes
{
    /// <summary>預設 scheme：看請求帶了哪種 header，轉給對應的 scheme（見 <see cref="AuthExtensions"/>）。</summary>
    public const string Default = "Gateway";
    public const string ApiKey = "ApiKey";
    public const string AdminToken = "AdminToken";
    public const string CloudflareAccess = "CloudflareAccess";
}
