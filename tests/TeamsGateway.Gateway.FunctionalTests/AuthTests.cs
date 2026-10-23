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
    public async Task Auth_refresh_without_cookies_reports_why()
    {
        // 強制重換：沒有 cookie 就該明講換不到（502 + 原因），不是 500
        var resp = await fx.CreateAdminClient().PostAsync("/admin/auth/refresh", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Contains("還沒匯入登入 cookie", await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
