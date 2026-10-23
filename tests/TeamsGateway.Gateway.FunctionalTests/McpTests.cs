using ModelContextProtocol.Client;

namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>/mcp 整條走 HTTP：用 SDK 的 MCP client 連上來、列 tool。tool 改成明確註冊（不掃 assembly）後要一個都不少，參數 schema 也要產得出來。</summary>
public sealed class McpTests(GatewayAppFixture fx) : IClassFixture<GatewayAppFixture>
{
    [Fact]
    public async Task Lists_tools_with_parameter_schemas()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, apiKey) = await fx.CreateApiKeyAsync(ct);
        var http = fx.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
        }, http, ownsHttpClient: true);

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);

        Assert.Equal(["list_chats", "read_messages", "send_message"], tools.Select(t => t.Name).Order());
        var send = tools.Single(t => t.Name == "send_message");
        var properties = send.JsonSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("chat", out _));
        Assert.True(properties.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task Requires_api_key()
    {
        var resp = await fx.CreateClient().PostAsync("/mcp", new StringContent("{}"), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
