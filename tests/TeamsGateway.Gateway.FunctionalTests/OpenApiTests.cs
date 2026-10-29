using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>
/// TypedResults 讓 OpenAPI 文件帶得出 response schema（之前回匿名物件，/scalar 上看不到回應長相）。
/// fixture 關掉了 reflection，所以文件產得出來也代表 schema 用到的型別都在 source-gen context 裡。
/// </summary>
public sealed class OpenApiTests(GatewayAppFixture fx) : IClassFixture<GatewayAppFixture>
{
    [Theory]
    [InlineData("/v1/status", "get", "200")]
    [InlineData("/v1/chats", "get", "200")]
    [InlineData("/v1/chats/{id}/messages", "get", "200")]
    [InlineData("/v1/chats/{id}/messages", "get", "400")]
    [InlineData("/v1/chats/{id}/messages/{messageId}", "put", "409")]
    [InlineData("/v1/chats/{id}/messages/{messageId}", "delete", "404")]
    [InlineData("/v1/messages/{messageId}/revisions", "get", "200")]
    [InlineData("/v1/webhooks", "post", "200")]
    [InlineData("/admin/api-keys", "post", "200")]
    [InlineData("/admin/subscriptions", "get", "200")]
    public async Task Responses_have_json_schemas(string path, string method, string status)
    {
        var ct = TestContext.Current.CancellationToken;
        using var doc = JsonDocument.Parse(await fx.CreateClient().GetStringAsync("/openapi/v1.json", ct));

        var response = doc.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("responses").GetProperty(status);

        Assert.True(response.GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _));
    }

    // 根目錄轉到 API doc：Aspire dashboard 上 gateway 的連結點進去就是文件
    [Fact]
    public async Task Root_redirects_to_scalar()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = fx.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/", ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/scalar", response.Headers.Location?.OriginalString);
    }
}
