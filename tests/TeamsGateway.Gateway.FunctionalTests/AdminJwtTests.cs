using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TeamsGateway.Gateway.Auth;

namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>
/// Cloudflare Access 的身分 JWT（ADR 0011）：Access 只負責是誰，admin 看 Gateway:SuperAdmins。
/// 用自己的 RSA 金鑰簽 JWT，certs 端點換成回這把公鑰的假 handler。這組只設 JWT、沒設 admin token，順便驗兩條路互不依賴。
/// </summary>
public sealed class AdminJwtTests(AdminJwtTests.Fixture fx) : IClassFixture<AdminJwtTests.Fixture>
{
    [Fact]
    public async Task Super_admin_can_use_admin_endpoints()
    {
        var resp = await fx.Get("/admin/api-keys", fx.Token("Owner@Example.com"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // email 不分大小寫
    }

    // Cloudflare 只負責「是誰」：登入成功但不在名單上的一律擋
    [Fact]
    public async Task Signed_in_but_not_a_super_admin_is_403()
    {
        var resp = await fx.Get("/admin/api-keys", fx.Token("someone@example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // 沒有 email 的（例如 service token 的 JWT）認得出來、但對不到名單：有身分、沒有角色
    [Fact]
    public async Task Jwt_without_email_is_403()
    {
        var resp = await fx.Get("/admin/api-keys", fx.Token(email: null));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // 同一個 team 其他 Access app 的 JWT、別的 team 簽的、過期的、別把金鑰簽的都不算是誰
    [Theory]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("expired")]
    [InlineData("other-key")]
    public async Task Invalid_jwt_is_401(string what)
    {
        var token = what switch
        {
            "audience" => fx.Token(Fixture.Admin, audience: "another-app"),
            "issuer" => fx.Token(Fixture.Admin, issuer: "https://other-team.cloudflareaccess.com"),
            "expired" => fx.Token(Fixture.Admin, expires: DateTime.UtcNow.AddMinutes(-10)),
            "other-key" => fx.Token(Fixture.Admin, key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = Fixture.KeyId }),
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };

        var resp = await fx.Get("/admin/api-keys", token);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // 沒設 admin token 時那條路是關的：帶什麼 token 都沒用
    [Fact]
    public async Task Admin_token_is_ignored_when_not_configured()
    {
        var client = fx.CreateClient();
        client.DefaultRequestHeaders.Add(AdminTokenAuthenticationHandler.HeaderName, "");
        var resp = await client.GetAsync("/admin/api-keys", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    public sealed class Fixture : GatewayAppFixture
    {
        public const string Issuer = "https://test-team.cloudflareaccess.com";
        public const string Audience = "test-aud";
        public const string KeyId = "test-key";
        public const string Admin = "owner@example.com";

        private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = KeyId };

        protected override void ConfigureGateway(IWebHostBuilder builder)
        {
            builder.UseSetting("Gateway:AdminToken", "");
            builder.UseSetting("Gateway:CloudflareAccess:Issuer", Issuer + "/"); // 結尾的 / 要被忽略
            builder.UseSetting("Gateway:CloudflareAccess:Audience", Audience);
            builder.UseSetting("Gateway:SuperAdmins:0", Admin);
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(CloudflareAccess.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new CertsHandler(_key)));
        }

        public string Token(string? email = Admin, string audience = Audience, string issuer = Issuer, DateTime? expires = null, SecurityKey? key = null)
        {
            var now = DateTime.UtcNow;
            var expiresAt = expires ?? now.AddHours(1);
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                IssuedAt = expiresAt.AddHours(-1),
                NotBefore = expiresAt.AddHours(-1),
                Expires = expiresAt,
                Claims = email is null ? null : new Dictionary<string, object> { ["email"] = email },
                SigningCredentials = new SigningCredentials(key ?? _key, SecurityAlgorithms.RsaSha256),
            });
        }

        public Task<HttpResponseMessage> Get(string path, string jwt)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(CloudflareAccess.HeaderName, jwt);
            return client.GetAsync(path, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>假的 <c>{Issuer}/cdn-cgi/access/certs</c>：只回公鑰，格式照 Cloudflare 的 JWKS。</summary>
    private sealed class CertsHandler(RsaSecurityKey key) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.ToString() != $"{Fixture.Issuer}/cdn-cgi/access/certs")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var p = key.Rsa.ExportParameters(includePrivateParameters: false);
            var json = $$"""{"keys":[{"kid":"{{key.KeyId}}","kty":"RSA","alg":"RS256","use":"sig","e":"{{Base64UrlEncoder.Encode(p.Exponent)}}","n":"{{Base64UrlEncoder.Encode(p.Modulus)}}"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}

/// <summary>admin token 和 JWT 都沒設定：沒有人能用 admin。</summary>
public sealed class AdminDisabledTests(AdminDisabledTests.Fixture fx) : IClassFixture<AdminDisabledTests.Fixture>
{
    [Fact]
    public async Task Nobody_can_use_admin_when_nothing_is_configured()
    {
        var client = fx.CreateClient();
        client.DefaultRequestHeaders.Add(AdminTokenAuthenticationHandler.HeaderName, "anything");
        var resp = await client.PostAsJsonAsync("/admin/api-keys", new { name = "x" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    public sealed class Fixture : GatewayAppFixture
    {
        protected override void ConfigureGateway(IWebHostBuilder builder) => builder.UseSetting("Gateway:AdminToken", "");
    }
}
