using System.Net;
using System.Net.Http.Json;

namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>webhook 登記：目標網址不能拿 gateway 當跳板打內網 / metadata。</summary>
public sealed class WebhooksEndpointTests(GatewayAppFixture fx) : IClassFixture<GatewayAppFixture>
{
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1:8080/admin/api-keys")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/hook")]
    [InlineData("not a url")]
    public async Task Rejects_unsafe_targets(string targetUrl)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await fx.CreateApiClientAsync(ct);

        var resp = await client.PostAsJsonAsync("/v1/webhooks", new { targetUrl }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Accepts_public_https_target()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await fx.CreateApiClientAsync(ct);

        var resp = await client.PostAsJsonAsync("/v1/webhooks", new { targetUrl = "https://hooks.example.com/teams" }, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
