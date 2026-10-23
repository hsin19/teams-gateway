using Microsoft.AspNetCore.DataProtection;

namespace TeamsGateway.Gateway.Events;

/// <summary>
/// webhook 的 HMAC 密鑰加密後才存（跟登入 cookie 一樣用 DataProtection），DB 檔外流不等於能偽造簽章。
/// 加密過的帶 <see cref="Prefix"/>；這功能上線前存的是明文、沒有前綴，照原樣用。
/// </summary>
public sealed class WebhookSecretProtector(IDataProtectionProvider dp)
{
    private const string Prefix = "dp:";
    private readonly IDataProtector _protector = dp.CreateProtector("teams-gateway.webhook-secrets");

    public string? Protect(string? secret) => string.IsNullOrEmpty(secret) ? null : Prefix + _protector.Protect(secret);

    public string? Unprotect(string? stored) =>
        stored is null ? null
        : stored.StartsWith(Prefix, StringComparison.Ordinal) ? _protector.Unprotect(stored[Prefix.Length..])
        : stored;
}
