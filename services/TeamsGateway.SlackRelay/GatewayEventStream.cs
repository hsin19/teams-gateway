using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay;

/// <summary>SSE 的一個 message 事件。<see cref="Id"/> 是 gateway 的事件序號，重連時當 <c>Last-Event-ID</c> 帶回去。</summary>
public readonly record struct StreamEvent(string? Id, MessageDto Message);

/// <summary>
/// 連 gateway 的 <c>GET /v1/events</c>（SSE），解析出 <see cref="StreamEvent"/>。
/// 一次連線 yield 到斷為止；重連與 <c>Last-Event-ID</c> 的續接由呼叫端（worker）掌控。
/// </summary>
public sealed class GatewayEventStream(HttpClient http, ILogger<GatewayEventStream> logger, TimeSpan? idleTimeout = null)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // gateway 閒置時每 20s 送心跳：這麼久什麼都沒收到 = 連線半死（對方睡眠 / NAT 砍掉），丟例外讓 worker 重連。
    // 少了這個，half-open 的 TCP 會讓 ReadLineAsync 永遠卡住，relay 默默停擺
    private readonly TimeSpan _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(60);

    public async IAsyncEnumerable<StreamEvent> ConnectAsync(
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
        string? eventId = null;
        var data = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            string? line;
            // 每次讀各自計時：只算「等 gateway」的時間，呼叫端處理訊息（傳圖到 Slack 等）花多久都不算
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(_idleTimeout);
            try
            {
                line = await reader.ReadLineAsync(idle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"gateway SSE {_idleTimeout.TotalSeconds:0}s 沒有任何資料（連心跳都沒有），當作斷線");
            }
            if (line is null)
                break; // 連線被對方關掉

            if (line.Length == 0)
            {
                // 事件邊界：收完一個事件
                var message = eventType == "message" && data.Length > 0 ? TryParse(data.ToString()) : null;
                var id = eventId;
                eventType = null;
                eventId = null;
                data.Clear();
                if (message is not null)
                    yield return new StreamEvent(id, message);
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventType = line["event:".Length..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
                data.Append(line["data:".Length..].TrimStart());
            else if (line.StartsWith("id:", StringComparison.Ordinal))
                eventId = line["id:".Length..].Trim(); // 事件序號，不是訊息 id（編輯會沿用訊息 id）
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
