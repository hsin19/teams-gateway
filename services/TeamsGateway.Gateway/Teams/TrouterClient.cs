using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TeamsGateway.Contracts;

namespace TeamsGateway.Gateway.Teams;

/// <summary>Trouter 推給 gateway 的一則新訊息。</summary>
public sealed record IncomingMessage(MessageDto Message, string? ThreadTopic, string RawJson);

/// <summary>
/// 不透過瀏覽器，自己連 Trouter WebSocket、註冊推播、收新訊息。對照 spike/FINDINGS.md「即時事件」與 spike/trouter_client.py。
/// 一次 <see cref="RunAsync"/> 跑一條連線直到斷掉或取消；重連由 TrouterBackgroundService 負責。
/// </summary>
public sealed class TrouterClient(
    SkypeTokenProvider tokens,
    IHttpClientFactory httpFactory,
    ILogger<TrouterClient> logger)
{
    private const string TrouterUrl = "wss://go.trouter.skype.com/v4/c";
    private const string RegistrarUrl = "https://edge.skype.com/registrar/prod/v2/registrations";
    private static readonly string ClientInfo =
        JsonSerializer.Serialize(new { cv = "2026.39.01.1", ua = "TeamsCDL", hr = "", v = TeamsAuthConstants.ClientVersion });

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _seq;

    public async Task RunAsync(Func<IncomingMessage, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        var token = await tokens.GetAsync(ct);
        var epid = Guid.NewGuid().ToString();
        var query = $"?tc={Uri.EscapeDataString(ClientInfo)}&timeout=40&epid={epid}" +
                    $"&cor_id={Guid.NewGuid()}&con_num={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_0";

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(TrouterUrl + query), ct);
        logger.LogInformation("Trouter 已連線");

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ping = PingLoopAsync(ws, pingCts.Token);
        try
        {
            await ReceiveLoopAsync(ws, token.Token, epid, onMessage, ct);
        }
        finally
        {
            await pingCts.CancelAsync();
            await ping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, string skypeToken, string epid, Func<IncomingMessage, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        var buffer = new ArraySegment<byte>(new byte[64 * 1024]);
        var frame = new StringBuilder();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            frame.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogInformation("Trouter 關閉連線：{Status}", ws.CloseStatus);
                    return;
                }
                frame.Append(Encoding.UTF8.GetString(buffer.Array!, 0, result.Count));
            } while (!result.EndOfMessage);

            await HandleFrameAsync(ws, frame.ToString(), skypeToken, epid, onMessage, ct);
        }
    }

    private async Task HandleFrameAsync(ClientWebSocket ws, string frame, string skypeToken, string epid, Func<IncomingMessage, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        if (frame == "1::")
        {
            await SendAsync(ws, $"5:{NextSeq()}+::" + JsonSerializer.Serialize(new
            {
                name = "user.authenticate",
                args = new object[] { new { headers = new Dictionary<string, string> { ["X-Skypetoken"] = skypeToken } } },
            }), ct);
            return;
        }
        if (frame.StartsWith("5:", StringComparison.Ordinal))
        {
            var payload = JsonDocument.Parse(frame[(frame.IndexOf("::", StringComparison.Ordinal) + 2)..]);
            var name = payload.RootElement.GetProperty("name").GetString();
            if (name == "trouter.connected")
            {
                var surl = payload.RootElement.GetProperty("args")[0].GetProperty("surl").GetString()!;
                var status = await RegisterAsync(skypeToken, epid, surl, ct);
                logger.LogInformation("Trouter 註冊推播 HTTP {Status}", status);
            }
            else if (name == "trouter.message_loss")
            {
                var args = payload.RootElement.GetProperty("args");
                await SendAsync(ws, $"5:{NextSeq()}+::" + JsonSerializer.Serialize(new { name = "trouter.processed_message_loss", args }), ct);
            }
            return;
        }
        if (frame.StartsWith("3:::", StringComparison.Ordinal))
        {
            await HandlePushAsync(ws, frame[4..], onMessage, ct);
        }
    }

    private async Task HandlePushAsync(ClientWebSocket ws, string json, Func<IncomingMessage, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        using var req = JsonDocument.Parse(json);
        var root = req.RootElement;
        var id = root.GetProperty("id").GetInt64();
        var trouterRequest = root.TryGetProperty("headers", out var h) && h.TryGetProperty("trouter-request", out var tr) ? tr.GetString() : "";

        if (root.TryGetProperty("body", out var bodyEl) && bodyEl.GetString() is { } body && body.StartsWith('{'))
        {
            using var envelope = JsonDocument.Parse(body);
            var env = envelope.RootElement;
            // NewMessage 是新訊息；編輯 / 刪除可能走別的 resourceType（MessageUpdate 等），一併收，靠 version upsert 決定更新
            var resourceType = env.TryGetProperty("resourceType", out var rt) ? rt.GetString() : null;
            if (resourceType is "NewMessage" or "MessageUpdate" or "EditMessage" && env.TryGetProperty("resource", out var resource))
            {
                var conversationId = resource.TryGetProperty("to", out var to) ? to.GetString() : null;
                var dto = conversationId is null ? null : ChatsvcClient.ParseMessage(resource, conversationId);
                if (dto is not null && dto.Type is "Text" or "RichText/Html")
                {
                    var topic = resource.TryGetProperty("threadtopic", out var tt) ? tt.GetString() : null;
                    await onMessage(new IncomingMessage(dto, topic, resource.GetRawText()), ct);
                }
            }
        }

        // 一定要 ack，否則 Trouter 會重送
        await SendAsync(ws, "3:::" + JsonSerializer.Serialize(new
        {
            id,
            status = 200,
            headers = new Dictionary<string, string> { ["trouter-request"] = trouterRequest ?? "", ["trouter-client"] = "{\"cd\":1}" },
            body = "",
        }), ct);
    }

    private async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                if (ws.State == WebSocketState.Open)
                    await SendAsync(ws, $"5:{NextSeq()}+::{{\"name\":\"ping\"}}", ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<int> RegisterAsync(string skypeToken, string epid, string surl, CancellationToken ct)
    {
        var body = new
        {
            clientDescription = new
            {
                appId = "TeamsCDLWebWorker",
                aesKey = "",
                languageId = "en-US",
                platform = "chrome",
                templateKey = "TeamsCDLWebWorker_2.7",
                platformUIVersion = TeamsAuthConstants.ClientVersion,
                productContext = "TFL",
            },
            registrationId = epid,
            nodeId = "",
            transports = new { TROUTER = new[] { new { context = "", path = surl, ttl = 3600 } } },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, RegistrarUrl)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("X-Skypetoken", skypeToken);
        using var resp = await httpFactory.CreateClient().SendAsync(request, ct);
        return (int)resp.StatusCode;
    }

    private async Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private int NextSeq() => Interlocked.Increment(ref _seq);
}
