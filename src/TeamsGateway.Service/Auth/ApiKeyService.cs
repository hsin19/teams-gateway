using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TeamsGateway.Service.Data;

namespace TeamsGateway.Service.Auth;

/// <summary>訂閱者的 API key：建立時回傳一次明文，DB 只存 SHA-256 hash。</summary>
public sealed class ApiKeyService(IDbContextFactory<GatewayDbContext> factory)
{
    public const string Prefix = "tgw_";

    public async Task<(ApiKey Key, string Raw)> CreateAsync(string name, CancellationToken ct = default)
    {
        var raw = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = name,
            KeyHash = Hash(raw),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        return (key, raw);
    }

    public async Task<ApiKey?> ValidateAsync(string raw, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var hash = Hash(raw);
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null, ct);
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.ApiKeys.OrderBy(k => k.CreatedAt).ToListAsync(ct);
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = await db.ApiKeys.FindAsync([id], ct);
        if (key is null || key.RevokedAt is not null)
            return false;
        key.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
