using Microsoft.AspNetCore.DataProtection;

namespace TeamsGateway.Gateway.Events;

/// <summary>
/// webhook 的 HMAC 密鑰加密後才存（跟登入 cookie 一樣用 DataProtection），DB 檔外流不等於能偽造簽章。
/// 存的值帶 <see cref="Prefix"/>，認得出是這種格式。
/// </summary>
public sealed class WebhookSecretProtector(IDataProtectionProvider dp)
{
    private const string Prefix = "dp:";
    private readonly IDataProtector _protector = dp.CreateProtector("teams-gateway.webhook-secrets");

    public string? Protect(string? secret) => string.IsNullOrEmpty(secret) ? null : Prefix + _protector.Protect(secret);

    public string? Unprotect(string? stored) =>
        stored is null ? null
        : stored.StartsWith(Prefix, StringComparison.Ordinal) ? _protector.Unprotect(stored[Prefix.Length..])
        : throw new InvalidOperationException("The stored webhook secret is not in the encrypted format");
}
