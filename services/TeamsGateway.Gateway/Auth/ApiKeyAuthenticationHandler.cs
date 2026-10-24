using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TeamsGateway.Gateway.Auth;

/// <summary>
/// API key（<c>Authorization: Bearer tgw_...</c> 或 <c>X-Api-Key</c>）。認得的 key 給 <see cref="GatewayRoles.Api"/>，
/// 並帶上 key 的 id（<see cref="CurrentUser.ApiKeyId"/>：SSE 確認沒被撤銷、webhook 依 key 分開管）。
/// 不是 <c>tgw_</c> 開頭的不歸它管（NoResult），留給其他 scheme。
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeyService keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string KeyIdClaim = "api_key_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var raw = ExtractKey(Request);
        if (raw is null || !raw.StartsWith(ApiKeyService.Prefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var key = await keys.ValidateAsync(raw, Context.RequestAborted);
        if (key is null)
            return AuthenticateResult.Fail("Unknown or revoked API key");

        var identity = new ClaimsIdentity(
            [
                new Claim(KeyIdClaim, key.Id.ToString()),
                new Claim(ClaimTypes.Name, key.Name),
                new Claim(ClaimTypes.Role, GatewayRoles.Api),
            ],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private static string? ExtractKey(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        var header = request.Headers["X-Api-Key"].ToString();
        return string.IsNullOrEmpty(header) ? null : header;
    }
}
