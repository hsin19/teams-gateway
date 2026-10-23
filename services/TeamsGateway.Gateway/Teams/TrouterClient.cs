using System.Buffers;
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
    internal static readonly string ClientInfo = JsonSerializer.Serialize(
        new TrouterClientInfo("2026.39.01.1", "TeamsCDL", "", TeamsAuthConstants.ClientVersion), TeamsJsonContext.Default.TrouterClientInfo);

    // 推播註冊的 ttl 是 3600s：不續約一小時後 Teams 就不再推，而連線本身還活著，keepalive 抓不到
    private static readonly TimeSpan RenewInterval = TimeSpan.FromMinutes(40);

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _seq;
    private string? _surl;

    public async Task RunAsync(Func<IncomingMessage, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        _surl = null;
        var token = await tokens.GetAsync(ct);
        var epid = Guid.NewGuid().ToString();
        var query = $"?tc={Uri.EscapeDataString(ClientInfo)}&timeout=40&epid={epid}" +
                    $"&cor_id={Guid.NewGuid()}&con_num={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_0";

        using var ws = new ClientWebSocket();
        // 偵測 half-open 死連線：定期送 WS ping，對方沒回 pong 就 abort，ReceiveAsync 會丟例外 → 觸發重連。
        // 少了這個，TCP 靜默斷線時 ReceiveAsync 會永遠卡住、收不到也不重連。
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        await ws.ConnectAsync(new Uri(TrouterUrl + query), ct);
        logger.LogInformation("Trouter 已連線");

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ping = PingLoopAsync(ws, pingCts.Token);
        var renew = RenewRegistrationLoopAsync(ws, epid, pingCts.Token);
        try
        {
            await ReceiveLoopAsync(ws, token.Token, epid, onMessage, ct);
        }
        finally
        {
            await pingCts.CancelAsync();
            await ping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await renew.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>到期前定期續約推播註冊；續約失敗就 abort 連線，讓背景服務重連（重連會重新註冊）。</summary>
    private async Task RenewRegistrationLoopAsync(ClientWebSocket ws, string epid, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RenewInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_surl is not { } surl)
                continue; // 還沒收到 trouter.connected

            try
            {
                var token = await tokens.GetAsync(ct);
                var status = await RegisterAsync(token.Token, epid, surl, ct);
                if (status is >= 200 and < 300)
                {
                    logger.LogInformation("Trouter 推播註冊續約 HTTP {Status}", status);
                    continue;
                }
                logger.LogWarning("Trouter 推播註冊續約 HTTP {Status}，重連", status);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Trouter 推播註冊續約失敗，重連");
            }
            ws.Abort();
            return;
        }
    }

    internal async Task ReceiveLoopAsync(WebSocket ws, string skypeToken, string epid, Func<IncomingMessage, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var message = new ArrayBufferWriter<byte>();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            if (await ReceiveTextAsync(ws, buffer, message, ct) is not { } frame)
            {
                logger.LogInformation("Trouter 關閉連線：{Status}", ws.CloseStatus);
                return;
            }
            try
            {
                await HandleFrameAsync(ws, frame, skypeToken, epid, onMessage, ct);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            {
                // 格式認不得的 frame 跳過就好，不要拖垮整條連線（漏掉的訊息輪詢會補）
                logger.LogWarning(ex, "Trouter frame 解析失敗，略過：{Frame}", frame.Length <= 300 ? frame : frame[..300]);
            }
        }
    }

    /// <summary>
    /// 收完一則完整的 WebSocket 訊息（可能分好幾段）再一次做 UTF-8 解碼；對方關閉回 null。
    /// 不能每段各自解碼：ReceiveAsync 可能在任意 byte 邊界切開，中文字（3 bytes）被切到就會變成 U+FFFD 存進 DB。
    /// </summary>
    internal static async Task<string?> ReceiveTextAsync(WebSocket ws, byte[] buffer, ArrayBufferWriter<byte> message, CancellationToken ct)
    {
        message.ResetWrittenCount();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            message.Write(buffer.AsSpan(0, result.Count));
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.WrittenSpan);
    }

    private async Task HandleFrameAsync(WebSocket ws, string frame, string skypeToken, string epid, Func<IncomingMessage, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        if (frame == "1::")
        {
            await SendAsync(ws, $"5:{NextSeq()}+::" + AuthenticateJson(skypeToken), ct);
            return;
        }
        if (frame.StartsWith("5:", StringComparison.Ordinal))
        {
            using var payload = JsonDocument.Parse(frame[(frame.IndexOf("::", StringComparison.Ordinal) + 2)..]);
            var name = payload.RootElement.GetProperty("name").GetString();
            if (name == "trouter.connected")
            {
                var surl = payload.RootElement.GetProperty("args")[0].GetProperty("surl").GetString()!;
                _surl = surl;
                var status = await RegisterAsync(skypeToken, epid, surl, ct);
                if (status is >= 200 and < 300)
                    logger.LogInformation("Trouter 註冊推播 HTTP {Status}", status);
                else
                    logger.LogWarning("Trouter 註冊推播 HTTP {Status}，收不到推播，靠輪詢補", status);
            }
            else if (name == "trouter.message_loss")
            {
                var args = payload.RootElement.GetProperty("args");
                await SendAsync(ws, $"5:{NextSeq()}+::" + MessageLossAckJson(args), ct);
            }
            return;
        }
        if (frame.StartsWith("3:::", StringComparison.Ordinal))
        {
            await HandlePushAsync(ws, frame[4..], onMessage, ct);
        }
    }

    private async Task HandlePushAsync(WebSocket ws, string json, Func<IncomingMessage, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        using var req = JsonDocument.Parse(json);
        var root = req.RootElement;
        var id = root.GetProperty("id").GetInt64();
        var trouterRequest = root.TryGetProperty("headers", out var h) && h.TryGetProperty("trouter-request", out var tr) ? tr.GetString() : "";

        try
        {
            await DispatchPushAsync(root, onMessage, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 一則推播處理失敗（內容格式不認得、DB 一時寫不進去）不能拖垮整條連線；照樣 ack，漏掉的輪詢會補
            logger.LogWarning(ex, "Trouter 推播處理失敗，略過（輪詢會補）");
        }

        // 一定要 ack，否則 Trouter 會重送
        await SendAsync(ws, "3:::" + PushAckJson(id, trouterRequest ?? ""), ct);
    }

    internal static string AuthenticateJson(string skypeToken) => JsonSerializer.Serialize(
        new TrouterAuthenticate("user.authenticate", [new TrouterAuthArgs(new() { ["X-Skypetoken"] = skypeToken })]),
        TeamsJsonContext.Default.TrouterAuthenticate);

    internal static string MessageLossAckJson(JsonElement args) => JsonSerializer.Serialize(
        new TrouterMessageLossAck("trouter.processed_message_loss", args), TeamsJsonContext.Default.TrouterMessageLossAck);

    internal static string PushAckJson(long id, string trouterRequest) => JsonSerializer.Serialize(
        new TrouterPushAck(id, 200, new() { ["trouter-request"] = trouterRequest, ["trouter-client"] = "{\"cd\":1}" }, ""),
        TeamsJsonContext.Default.TrouterPushAck);

    private static async Task DispatchPushAsync(JsonElement root, Func<IncomingMessage, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        if (!root.TryGetProperty("body", out var bodyEl) || bodyEl.GetString() is not { } body || !body.StartsWith('{'))
            return;
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

    /// <summary>
    /// 跟網頁版 worker 一樣註冊兩筆：一般的 <c>TeamsCDLWebWorker_2.7</c>，再加 GoldenGate 雙重註冊
    /// （<c>_2.3</c>、registrationId 加 <c>_GG</c>、productContext 空、path 無結尾斜線）。
    /// 只有前者時只收得到自己帳號發的訊息（跨裝置同步），別人的訊息不會推來。
    /// 回傳兩筆中較差的 HTTP 狀態（都 2xx 才算成功）。
    /// </summary>
    private async Task<int> RegisterAsync(string skypeToken, string epid, string surl, CancellationToken ct)
    {
        var main = await PostRegistrationAsync(skypeToken, MainRegistration(epid, surl), ct);
        var goldenGate = await PostRegistrationAsync(skypeToken, GoldenGateRegistration(epid, surl), ct);
        return main is >= 200 and < 300 ? goldenGate : main;
    }

    internal static RegistrationRequest MainRegistration(string epid, string surl) => Registration(
        new TrouterClientDescription("TeamsCDLWebWorker", "", "en-US", "chrome", "TeamsCDLWebWorker_2.7", TeamsAuthConstants.ClientVersion, "TFL"),
        epid, surl);

    internal static RegistrationRequest GoldenGateRegistration(string epid, string surl) => Registration(
        new TrouterClientDescription("TeamsCDLWebWorker", "", "", "chrome", "TeamsCDLWebWorker_2.3", TeamsAuthConstants.ClientVersion, ""),
        $"{epid}_GG", surl.TrimEnd('/'));

    private static RegistrationRequest Registration(TrouterClientDescription client, string registrationId, string path) =>
        new(client, registrationId, NodeId: "", new RegistrationTransports([new TrouterTransport(Context: "", path, Ttl: 3600)]));

    private async Task<int> PostRegistrationAsync(string skypeToken, RegistrationRequest body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RegistrarUrl)
        {
            Content = JsonContent.Create(body, TeamsJsonContext.Default.RegistrationRequest),
        };
        request.Headers.TryAddWithoutValidation("X-Skypetoken", skypeToken);
        request.Headers.TryAddWithoutValidation("X-MS-Migration", "True");
        using var resp = await httpFactory.CreateClient().SendAsync(request, ct);
        return (int)resp.StatusCode;
    }

    private async Task SendAsync(WebSocket ws, string text, CancellationToken ct)
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
