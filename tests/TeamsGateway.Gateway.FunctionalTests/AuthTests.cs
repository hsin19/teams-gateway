using System.Net;
using System.Net.Http.Json;

namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>auth 閘門整條走 HTTP：/v1 要 API key、/admin 要 admin token。</summary>
public sealed class AuthTests(GatewayAppFixture fx) : IClassFixture<GatewayAppFixture>
{
    [Fact]
    public async Task V1_without_api_key_is_401()
    {
        // 無 key → filter 擋在前面，handler（會打 chatsvc）根本不會跑
        var resp = await fx.CreateClient().GetAsync("/v1/chats", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_without_token_is_401()
    {
        var resp = await fx.CreateClient().PostAsJsonAsync(
            "/admin/api-keys", new { name = "x" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Root_returns_liveness_text()
    {
        var resp = await fx.CreateClient().GetAsync("/", TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        Assert.Equal("teams-gateway", await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
