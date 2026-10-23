using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>
/// /admin 端點整條走 HTTP（不碰 Teams 的那些）。fixture 關掉了 reflection 序列化，所以這裡也驗證回應型別都進了 source-gen context，
/// 而且 JSON 長相跟改用具名 record 之前一樣。
/// </summary>
public sealed class AdminEndpointTests(GatewayAppFixture fx) : IClassFixture<GatewayAppFixture>
{
    [Fact]
    public async Task Login_cookies_import_then_auth_reports_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = fx.CreateAdminClient();

        var bad = await admin.PutAsync("/admin/login-cookies", new StringContent("not json", Encoding.UTF8, "application/json"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("不是合法的 cookie JSON 陣列", (await bad.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetString());

        var ok = await admin.PutAsync("/admin/login-cookies",
            new StringContent("""[{"name":"MSPAuth","value":"v","domain":"login.live.com","path":"/"}]""", Encoding.UTF8, "application/json"), ct);
        ok.EnsureSuccessStatusCode();
        Assert.Equal(1, (await ok.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("imported").GetInt32());

        var auth = await admin.GetFromJsonAsync<JsonElement>("/admin/auth", ct);
        Assert.True(auth.GetProperty("hasCookies").GetBoolean());
        Assert.Equal(JsonValueKind.Null, auth.GetProperty("skypeId").ValueKind);
    }

    [Fact]
    public async Task Api_keys_create_list_revoke()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = fx.CreateAdminClient();
        var (id, raw) = await fx.CreateApiKeyAsync(ct);
        Assert.StartsWith("tgw_", raw);

        var empty = await admin.PostAsJsonAsync("/admin/api-keys", new { name = " " }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var listed = (await admin.GetFromJsonAsync<JsonElement>("/admin/api-keys", ct)).EnumerateArray()
            .Single(k => k.GetProperty("id").GetGuid() == id);
        Assert.Equal("func", listed.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("revokedAt").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/admin/api-keys/{id}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/admin/api-keys/{id}", ct)).StatusCode);
    }

    [Fact]
    public async Task Subscriptions_list_and_unknown_unsubscribe()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = fx.CreateAdminClient();

        Assert.Equal(JsonValueKind.Array, (await admin.GetFromJsonAsync<JsonElement>("/admin/subscriptions", ct)).ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/admin/subscriptions/19:nope@thread.v2", ct)).StatusCode);
    }

    [Fact]
    public async Task Webhooks_list_and_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await fx.CreateApiClientAsync(ct);
        var created = await (await client.PostAsJsonAsync("/v1/webhooks", new { targetUrl = "https://hooks.example.com/x" }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        var id = created.GetProperty("id").GetGuid();
        Assert.False(created.GetProperty("hasSecret").GetBoolean());

        var listed = await client.GetFromJsonAsync<JsonElement>("/v1/webhooks", ct);
        Assert.Equal([id], listed.EnumerateArray().Select(h => h.GetProperty("id").GetGuid()));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/v1/webhooks/{id}", ct)).StatusCode);
    }

    [Fact]
    public async Task Status_reports_auth_health()
    {
        var ct = TestContext.Current.CancellationToken;
        var status = await (await fx.CreateApiClientAsync(ct)).GetFromJsonAsync<JsonElement>("/v1/status", ct);

        Assert.Equal("failed", status.GetProperty("teamsAuth").GetString()); // 還沒換過 token = 不健康
        Assert.True(status.TryGetProperty("tokenExpiresAt", out _));
    }
}
