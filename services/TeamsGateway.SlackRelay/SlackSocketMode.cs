using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// Slack Socket Mode（ADR 0012）：relay 主動連出去收 <c>app_mention</c>、訊息改 / 刪（ADR 0013）與按鈕事件，部署那台不用開對外的 HTTP。
/// 要 ack 的 envelope 先 ack（Slack 要 3 秒內），再丟給 <see cref="SlackSender"/> 在背景處理；
/// Slack 要求換連線（<c>disconnect</c>）就馬上重連，其他斷線退避後重連。沒設 <c>Relay:Slack:AppToken</c> 就不連，只轉送。
/// Native AOT 用不了 Slack 的 SDK，所以自己接 <see cref="ClientWebSocket"/>。
/// </summary>
public sealed class SlackSocketMode(SlackClient slack, SlackSender sender, IOptions<RelayOptions> options, ILogger<SlackSocketMode> logger)
    : BackgroundService, ISlackSocket
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private volatile bool _connected;

    /// <summary>連著、而且收過 Slack 的 hello（給 <c>/health</c>）。</summary>
    public bool Connected => _connected;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var appToken = options.Value.Slack.AppToken;
        if (string.IsNullOrWhiteSpace(appToken))
        {
            logger.LogInformation("Relay:Slack:AppToken is not set; sending from Slack to Teams is off");
            return;
        }

        var backoff = MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            var refresh = false;
            var greeted = false;
            try
            {
                if (await slack.OpenSocketAsync(appToken, ct) is { } url)
                {
                    using var ws = new ClientWebSocket();
                    // Slack 的連線偶爾半死不關：自己送 ping，等不到 pong 就斷，進下面重連
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
                    await ws.ConnectAsync(url, ct);
                    refresh = await ReceiveAsync(ws, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Slack Socket Mode connection dropped; reconnecting");
            }
            finally
            {
                greeted = _connected;
                _connected = false;
            }

            // 連上過（收過 hello）就從短的等起：用了幾小時後斷一次，不該跟連不上一樣越等越久
            if (greeted)
                backoff = MinBackoff;
            if (refresh)
                continue;
            await Task.Delay(backoff, ct);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    /// <summary>收到連線結束；回 true = Slack 要求換連線（馬上重連），false = 對方關掉。</summary>
    private async Task<bool> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        while (ws.State == WebSocketState.Open)
        {
            buffer.ResetWrittenCount();
            ValueWebSocketReceiveResult result;
            do
            {
                // 一則 envelope 可能分好幾個 frame
                result = await ws.ReceiveAsync(buffer.GetMemory(8 * 1024), ct);
                buffer.Advance(result.Count);
            } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

            if (result.MessageType == WebSocketMessageType.Close)
                return false;
            if (Parse(buffer.WrittenSpan) is not { } envelope)
                continue;

            switch (envelope.Type)
            {
                case "hello":
                    _connected = true;
                    logger.LogInformation("Connected to Slack Socket Mode");
                    break;
                case "disconnect":
                    logger.LogInformation("Slack asked to reconnect Socket Mode ({Reason})", envelope.Reason);
                    await CloseQuietlyAsync(ws);
                    return true;
                default:
                    if (envelope.EnvelopeId is { } id)
                        await ws.SendAsync(Ack(id), WebSocketMessageType.Text, endOfMessage: true, ct);
                    // 處理會打 Slack / gateway，不能卡住收下一則（ack 有時限）；例外在 HandleAsync 裡吞掉記 log
                    _ = Task.Run(() => sender.HandleAsync(envelope, ct), ct);
                    break;
            }
        }
        return false;
    }

    internal static SocketEnvelope? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, SlackJsonContext.Default.SocketEnvelope);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static byte[] Ack(string envelopeId) => JsonSerializer.SerializeToUtf8Bytes(new SocketAck(envelopeId), SlackJsonContext.Default.SocketAck);

    private static async Task CloseQuietlyAsync(ClientWebSocket ws)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reconnecting", timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // 要換連線了，舊的關不乾淨也沒關係
        }
    }
}

/// <summary>Socket Mode 連著沒（給 <c>/health</c>；測試換成假的）。</summary>
public interface ISlackSocket
{
    bool Connected { get; }
}
