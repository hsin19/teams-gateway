using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// 連 gateway 的 <c>GET /v1/events</c>（SSE），解析出 <see cref="MessageDto"/>。
/// 一次連線 yield 到斷為止；重連與 <c>Last-Event-ID</c> 的續接由呼叫端（worker）掌控。
/// </summary>
public sealed class GatewayEventStream(HttpClient http, ILogger<GatewayEventStream> logger)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async IAsyncEnumerable<MessageDto> ConnectAsync(
        string? chats, string? lastEventId, [EnumeratorCancellation] CancellationToken ct)
    {
        var url = "/v1/events";
        if (!string.IsNullOrEmpty(chats))
            url += "?chats=" + Uri.EscapeDataString(chats);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        if (!string.IsNullOrEmpty(lastEventId))
            req.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        logger.LogInformation("已連上 gateway SSE（chats={Chats}）", chats ?? "全部");

        string? eventType = null;
        var data = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break; // 連線被對方關掉

            if (line.Length == 0)
            {
                // 事件邊界：收完一個事件
                var message = eventType == "message" && data.Length > 0 ? TryParse(data.ToString()) : null;
                eventType = null;
                data.Clear();
                if (message is not null)
                    yield return message;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventType = line["event:".Length..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
                data.Append(line["data:".Length..].TrimStart());
            // id: 行不另外存，我們用 message.Id 當 Last-Event-ID
        }
    }

    private MessageDto? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MessageDto>(json, Web);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "SSE data 解析失敗，跳過");
            return null;
        }
    }
}
