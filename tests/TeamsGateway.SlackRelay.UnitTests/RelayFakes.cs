using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>測試用：接到假 Slack（<see cref="RecordingSlack"/> 等 handler）的 client 與相依物件。</summary>
internal static class RelayFakes
{
    public static SlackClient Slack(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://slack.com/api/") }, NullLogger<SlackClient>.Instance);

    public static SendTracker Sends(HttpMessageHandler slack, MessageLinks links, RelayOptions? options = null) =>
        new(Slack(slack), links, Options.Create(options ?? new RelayOptions()), NullLogger<SendTracker>.Instance);

    public static HttpClient Gateway(FakeGateway gateway) => new(gateway) { BaseAddress = new Uri("http://gateway.test") };
}

/// <summary>
/// 假的 gateway：記下每個請求（method、路徑、body），依「method 路徑」決定回應（狀態碼 + JSON）；沒設的回 200 + <c>{}</c>。
/// </summary>
internal sealed class FakeGateway : HttpMessageHandler
{
    public List<(string Method, string Path, string Body)> Requests { get; } = [];

    /// <summary>「POST /v1/chats/19:a@thread.v2/messages」→（狀態碼, JSON）。路徑是解碼過的。</summary>
    public Dictionary<string, (HttpStatusCode Status, string Body)> Responses { get; } = [];

    /// <summary>依整個請求決定回應（要等待、或依 method / query 分流時用）；回 null 就照 <see cref="Responses"/>。</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<(HttpStatusCode Status, string Body)?>>? RespondAsync { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method.Method, path, body));
        if (RespondAsync is not null && await RespondAsync(request, cancellationToken) is { } response)
            return new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body, Encoding.UTF8, "application/json") };
        var (status, json) = Responses.TryGetValue($"{request.Method.Method} {path}", out var canned) ? canned : (HttpStatusCode.OK, "{}");
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
