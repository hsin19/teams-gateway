using System.Net;
using System.Text;
using System.Text.Json;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>
/// 假的 Slack Web API：記下每個呼叫（method + JSON body），一律回成功；chat.getPermalink 回由 ts 組成的 permalink。
/// 要特定回應（例如 conversations.replies 讀到的 thread）就放進 <see cref="Responses"/>。
/// </summary>
internal sealed class RecordingSlack : HttpMessageHandler
{
    public static string Permalink(string ts) => $"https://example.slack.com/archives/C1/p{ts.Replace(".", "")}";

    public List<(string Method, JsonElement Body)> Requests { get; } = [];

    /// <summary>method → 回應的 JSON。</summary>
    public Dictionary<string, string> Responses { get; } = [];

    /// <summary>依完整網址決定回應（同一個 method、參數不同時用）；回 null 就照 <see cref="Responses"/> / 預設。</summary>
    public Func<Uri, string?>? Respond { get; set; }

    /// <summary>依 method 與 JSON body 決定回應（例如某個表情名稱回 invalid_name）；回 null 就照 <see cref="Respond"/> 以下的規則。</summary>
    public Func<string, JsonElement, string?>? RespondToBody { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.RequestUri!.Segments[^1];
        var body = request.Content is null
            ? default
            : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
        Requests.Add((method, body));
        var response = RespondToBody?.Invoke(method, body) ?? Respond?.Invoke(request.RequestUri) ?? (Responses.TryGetValue(method, out var canned) ? canned
            : method == "chat.getPermalink"
            ? $$"""{"ok":true,"permalink":"{{Permalink(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["message_ts"]!)}}"}"""
            : """{"ok":true,"ts":"111.333"}""");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
    }
}
