using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TeamsGateway.Gateway.Auth;

/// <summary>
/// admin token（<c>X-Admin-Token</c> 等於 <c>Gateway:AdminToken</c>）：部署環境內部打 <c>/admin</c> 用（bootstrap、建 key），
/// 給 <see cref="GatewayRoles.Admin"/>。沒設 <c>Gateway:AdminToken</c> 時這條路關閉，帶什麼都不認。
/// </summary>
public sealed class AdminTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IOptions<GatewayOptions> gateway)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string HeaderName = "X-Admin-Token";

    private readonly string? _adminToken = gateway.Value.AdminToken;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (string.IsNullOrEmpty(_adminToken) || !Request.Headers.TryGetValue(HeaderName, out var provided))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!FixedTimeEquals(provided.ToString(), _adminToken))
            return Task.FromResult(AuthenticateResult.Fail("Wrong admin token"));

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin-token"), new Claim(ClaimTypes.Role, GatewayRoles.Admin)],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
