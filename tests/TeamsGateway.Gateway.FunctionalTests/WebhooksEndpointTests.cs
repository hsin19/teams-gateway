using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;

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
    public async Task Accepts_public_https_target_and_stores_secret_encrypted()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await fx.CreateApiClientAsync(ct);
        var secret = "s3cret-" + Guid.NewGuid().ToString("N");

        var resp = await client.PostAsJsonAsync("/v1/webhooks", new { targetUrl = "https://hooks.example.com/teams", secret }, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        await using var scope = fx.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<GatewayDbContext>>().CreateDbContextAsync(ct);
        var stored = (await db.WebhookSubscriptions.SingleAsync(w => w.Id == id, ct)).Secret;
        Assert.DoesNotContain(secret, stored); // DB 裡不是明文
        Assert.Equal(secret, scope.ServiceProvider.GetRequiredService<WebhookSecretProtector>().Unprotect(stored));
    }
}
