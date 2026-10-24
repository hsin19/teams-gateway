using System.Security.Claims;
using TeamsGateway.Gateway.Auth;

namespace TeamsGateway.Gateway.UnitTests;

public class AuthTests
{
    // 設了 Issuer 卻沒 Audience：同一個 team 其他 Access app 的 JWT 也會被接受，寧可起不來
    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Cloudflare_issuer_without_audience_is_rejected(string? audience)
    {
        var settings = new CloudflareAccessSettings { Issuer = "https://team.cloudflareaccess.com", Audience = audience };

        Assert.Throws<InvalidOperationException>(() => CloudflareAccess.EnsureValid(settings));
    }

    // 沒設 Issuer = 不認這種身分，其他欄位不用管；有 Issuer 也有 Audience 才算設好
    [Fact]
    public void Cloudflare_settings_disabled_or_complete_are_accepted()
    {
        CloudflareAccess.EnsureValid(null);
        CloudflareAccess.EnsureValid(new CloudflareAccessSettings());
        CloudflareAccess.EnsureValid(new CloudflareAccessSettings { Issuer = "https://team.cloudflareaccess.com", Audience = "aud" });
    }

    // scheme 直接給的角色（API key 的 api）不用查名單：之後名單放 DB，只要 api 的請求也不會多一次查詢
    [Fact]
    public async Task Role_from_the_scheme_does_not_touch_the_directory()
    {
        var directory = new CountingDirectory();
        var user = User(directory, new Claim(ClaimTypes.Role, GatewayRoles.Api), new Claim("email", "owner@example.com"));

        Assert.True(await user.IsInRoleAsync(GatewayRoles.Api));
        Assert.Equal(0, directory.Calls);
    }

    // 只知道 email 的身分才查名單，同一個請求查一次
    [Fact]
    public async Task Email_identity_asks_the_directory_once_per_request()
    {
        var directory = new CountingDirectory(admins: "owner@example.com");
        var user = User(directory, new Claim("email", "owner@example.com"));

        Assert.True(await user.IsInRoleAsync(GatewayRoles.Admin));
        Assert.False(await user.IsInRoleAsync(GatewayRoles.Api));
        Assert.Equal(1, directory.Calls);
    }

    // 沒有 email 也沒有角色（例如 service token 的 JWT）：什麼角色都沒有，也不查
    [Fact]
    public async Task Identity_without_email_or_role_has_no_roles()
    {
        var directory = new CountingDirectory();
        var user = User(directory, new Claim("sub", "service-token"));

        Assert.False(await user.IsInRoleAsync(GatewayRoles.Admin));
        Assert.Equal(0, directory.Calls);
    }

    private static CurrentUser User(IRoleDirectory directory, params Claim[] claims)
    {
        var user = new CurrentUser(directory);
        user.Set(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), TestContext.Current.CancellationToken);
        return user;
    }

    private sealed class CountingDirectory(params string[] admins) : IRoleDirectory
    {
        public int Calls { get; private set; }

        public ValueTask<IReadOnlySet<string>> GetRolesAsync(string email, CancellationToken ct)
        {
            Calls++;
            IReadOnlySet<string> roles = admins.Contains(email) ? new HashSet<string> { GatewayRoles.Admin } : new HashSet<string>();
            return ValueTask.FromResult(roles);
        }
    }
}
