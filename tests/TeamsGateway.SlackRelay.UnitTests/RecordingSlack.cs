using System.Net;
using System.Text;
using System.Text.Json;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>假的 Slack Web API：記下每個呼叫（method + JSON body），一律回成功；chat.getPermalink 回由 ts 組成的 permalink。</summary>
internal sealed class RecordingSlack : HttpMessageHandler
{
    public static string Permalink(string ts) => $"https://example.slack.com/archives/C1/p{ts.Replace(".", "")}";

    public List<(string Method, JsonElement Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.RequestUri!.Segments[^1];
        var body = request.Content is null
            ? default
            : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
        Requests.Add((method, body));
        var response = method == "chat.getPermalink"
            ? $$"""{"ok":true,"permalink":"{{Permalink(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["message_ts"]!)}}"}"""
            : """{"ok":true,"ts":"111.333"}""";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
    }
}
