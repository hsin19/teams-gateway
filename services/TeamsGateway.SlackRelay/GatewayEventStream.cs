using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay;

/// <summary>SSE 的一個事件：訊息或表情變動擇一。<see cref="Id"/> 是 gateway 的事件序號，重連時當 <c>Last-Event-ID</c> 帶回去。</summary>
public readonly record struct StreamEvent(string? Id, MessageDto? Message = null, ReactionDto? Reaction = null);

/// <summary>
/// 連 gateway 的 <c>GET /v1/events</c>（SSE），解析出 <see cref="StreamEvent"/>。
/// 一次連線 yield 到斷為止；重連與 <c>Last-Event-ID</c> 的續接由呼叫端（worker）掌控。
/// </summary>
public sealed class GatewayEventStream(HttpClient http, ILogger<GatewayEventStream> logger, TimeSpan? idleTimeout = null)
{
    // gateway 閒置時每 20s 送心跳：這麼久什麼都沒收到 = 連線半死（對方睡眠 / NAT 砍掉），丟例外讓 worker 重連。
    // 少了這個，half-open 的 TCP 會讓 ReadLineAsync 永遠卡住，relay 默默停擺
    private readonly TimeSpan _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(60);

    private volatile bool _connected;

    /// <summary>
    /// 現在正連著 gateway 的 SSE（給 <c>/health</c>）。半死的連線最多閒置逾時之後就會丟例外、變回 false，
    /// 不會一直掛著 true。
    /// </summary>
    public bool Connected => _connected;

    /// <param name="afterMessage">沒有 <paramref name="lastEventId"/> 時（剛重啟），從這則 Teams 訊息之後續接。</param>
    public async IAsyncEnumerable<StreamEvent> ConnectAsync(
        string? chats, string? lastEventId, string? afterMessage, [EnumeratorCancellation] CancellationToken ct)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(chats))
            query.Add("chats=" + Uri.EscapeDataString(chats));
        if (string.IsNullOrEmpty(lastEventId) && !string.IsNullOrEmpty(afterMessage))
            query.Add("afterMessage=" + Uri.EscapeDataString(afterMessage));
        var url = query.Count == 0 ? "/v1/events" : "/v1/events?" + string.Join('&', query);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        if (!string.IsNullOrEmpty(lastEventId))
            req.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        logger.LogInformation("已連上 gateway SSE（chats={Chats}）", chats ?? "全部");
        _connected = true;
        try
        {
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
                    throw new TimeoutException($"No data from the gateway SSE for {_idleTimeout.TotalSeconds:0}s (not even a heartbeat); treating it as disconnected");
                }
                if (line is null)
                    break; // 連線被對方關掉

                if (line.Length == 0)
                {
                    // 事件邊界：收完一個事件
                    var parsed = data.Length == 0 ? null : eventType switch
                    {
                        EventKinds.Message when TryParse(data.ToString(), ContractsJsonContext.Default.MessageDto) is { } message => new StreamEvent(eventId, message),
                        EventKinds.Reaction when TryParse(data.ToString(), ContractsJsonContext.Default.ReactionDto) is { } reaction => new StreamEvent(eventId, Reaction: reaction),
                        _ => (StreamEvent?)null, // 不認得的事件種類：略過
                    };
                    eventType = null;
                    eventId = null;
                    data.Clear();
                    if (parsed is { } evt)
                        yield return evt;
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
        finally
        {
            _connected = false;
        }
    }

    private T? TryParse<T>(string json, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "SSE data 解析失敗，跳過");
            return null;
        }
    }
}
