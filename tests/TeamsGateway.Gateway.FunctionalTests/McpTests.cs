using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>/mcp 整條走 HTTP：用 SDK 的 MCP client 連上來、列 tool、呼叫 tool。tool 改成明確註冊（不掃 assembly）後要一個都不少，參數 schema 也要產得出來。</summary>
public sealed class McpTests(GatewayAppFixture fx) : IClassFixture<GatewayAppFixture>
{
    [Fact]
    public async Task Lists_tools_with_parameter_schemas()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);

        Assert.Equal(["get_attachments", "list_chats", "read_messages", "send_message"], tools.Select(t => t.Name).Order());
        var send = tools.Single(t => t.Name == "send_message");
        var properties = send.JsonSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("chat", out _));
        Assert.True(properties.TryGetProperty("text", out _));
        var read = tools.Single(t => t.Name == "read_messages").JsonSchema.GetProperty("properties");
        Assert.True(read.TryGetProperty("since", out _));
        Assert.True(read.TryGetProperty("until", out _));
    }

    // SDK 只把 McpException 的訊息傳給 client，其他例外只剩一句通用錯誤：還沒匯入 cookie 時讀訊息，agent 要看得到原因
    [Fact]
    public async Task Tool_errors_tell_the_client_why()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync(ct);

        var result = await client.CallToolAsync("read_messages", new Dictionary<string, object?> { ["chat"] = "19:none@thread.v2" }, cancellationToken: ct);

        Assert.True(result.IsError);
        Assert.Contains("No login cookie imported yet", Text(result));
    }

    // 送 JSON：content type 不對的請求在挑 endpoint 時就回 415，輪不到授權，測不到 key
    [Fact]
    public async Task Requires_api_key()
    {
        var body = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var resp = await fx.CreateClient().PostAsync("/mcp", body, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    private async Task<McpClient> ConnectAsync(CancellationToken ct)
    {
        var (_, apiKey) = await fx.CreateApiKeyAsync(ct);
        var http = fx.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    private static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
