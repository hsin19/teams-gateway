using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace TeamsGateway.Gateway.Auth;

/// <summary>
/// Cloudflare Access 放行後附在請求上的身分 JWT（<c>Cf-Access-Jwt-Assertion</c>），用 JwtBearer 驗（ADR 0011）。
/// 只負責認證：產生帶 <c>email</c> 的身分、不給角色；角色由 <see cref="CurrentUser"/> 需要時才查。
/// </summary>
internal static class CloudflareAccess
{
    public const string HeaderName = "Cf-Access-Jwt-Assertion";
    public const string HttpClientName = "cloudflare-access";

    /// <summary>
    /// 設了 Issuer 就一定要有 Audience。不驗 audience 的話，同一個 team 其他 Access app 的 JWT 也進得來，
    /// 所以缺了就直接起不來，不要默默放行。
    /// </summary>
    public static void EnsureValid(CloudflareAccessSettings? settings)
    {
        if (!string.IsNullOrWhiteSpace(settings?.Issuer) && string.IsNullOrWhiteSpace(settings.Audience))
            throw new InvalidOperationException("Gateway:CloudflareAccess:Audience is required when Gateway:CloudflareAccess:Issuer is set");
    }

    public static void Configure(JwtBearerOptions options, CloudflareAccessSettings settings, IHttpClientFactory http)
    {
        var issuer = settings.Issuer!.TrimEnd('/');

        // 保留 JWT 原本的 claim 名稱（email），不要被換成 XML namespace 那種長名字
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = settings.Audience,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = "email",
        };

        // 公鑰在 {Issuer}/cdn-cgi/access/certs。交給 JwtBearer 的 ConfigurationManager 快取：
        // 每小時更新，遇到不認得的 kid（Cloudflare 輪替金鑰）提早重抓，但最多每分鐘一次
        options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{issuer}/cdn-cgi/access/certs", new CertsRetriever(), new HttpDocumentRetriever(http.CreateClient(HttpClientName)))
        {
            AutomaticRefreshInterval = TimeSpan.FromHours(1),
            RefreshInterval = TimeSpan.FromMinutes(1),
        };

        options.Events = new JwtBearerEvents
        {
            // 只讀 Cloudflare 的 header：Authorization 是 API key 的，沒帶這個 header 就不歸這個 scheme 管
            OnMessageReceived = context =>
            {
                var token = context.Request.Headers[HeaderName].ToString();
                if (token.Length == 0)
                    context.NoResult();
                else
                    context.Token = token;
                return Task.CompletedTask;
            },
        };
    }

    /// <summary>certs 端點只有 JWKS（不是 OIDC discovery 文件）：包成 configuration，讓 JwtBearer 的金鑰快取照常運作。</summary>
    private sealed class CertsRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
    {
        public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
        {
            var keys = new JsonWebKeySet(await retriever.GetDocumentAsync(address, cancel));
            var configuration = new OpenIdConnectConfiguration { JsonWebKeySet = keys };
            foreach (var key in keys.GetSigningKeys())
                configuration.SigningKeys.Add(key);
            return configuration;
        }
    }
}
