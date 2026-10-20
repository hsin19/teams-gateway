using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Teams;

/// <summary>把機敏字串（登入 cookie 等）用 DataProtection 加密後存進 <see cref="SecureValue"/>。</summary>
public sealed class SecureStore(IDbContextFactory<GatewayDbContext> factory, IDataProtectionProvider dp)
{
    private readonly IDataProtector _protector = dp.CreateProtector("teams-gateway.secure-values");

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.SecureValues.FindAsync([key], ct);
        return row is null ? null : _protector.Unprotect(row.Protected);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var protectedValue = _protector.Protect(value);
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.SecureValues.FindAsync([key], ct);
        if (row is null)
        {
            db.SecureValues.Add(new SecureValue { Key = key, Protected = protectedValue, UpdatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            row.Protected = protectedValue;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
