using System.Net.WebSockets;
using System.Text;

namespace TeamsGateway.Gateway.UnitTests;

/// <summary>依序吐出給定的訊息（每則可再切成多段），吐完就回 Close；記下送出去的文字。</summary>
internal sealed class FakeWebSocket : WebSocket
{
    private readonly Queue<(byte[] Bytes, bool End)> _fragments = new();
    private WebSocketState _state = WebSocketState.Open;

    public List<string> Sent { get; } = [];

    /// <summary>每則訊息一段。</summary>
    public static FakeWebSocket WithMessages(params string[] messages) =>
        WithFragments(messages.Select(m => new[] { Encoding.UTF8.GetBytes(m) }).ToArray());

    /// <summary>每則訊息由給定的片段組成（最後一段標 EndOfMessage）。</summary>
    public static FakeWebSocket WithFragments(params byte[][][] messages)
    {
        var ws = new FakeWebSocket();
        foreach (var fragments in messages)
            for (var i = 0; i < fragments.Length; i++)
                ws._fragments.Enqueue((fragments[i], i == fragments.Length - 1));
        return ws;
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (!_fragments.TryDequeue(out var fragment))
        {
            _state = WebSocketState.CloseReceived;
            return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        }
        fragment.Bytes.CopyTo(buffer.Array!, buffer.Offset);
        return Task.FromResult(new WebSocketReceiveResult(fragment.Bytes.Length, WebSocketMessageType.Text, fragment.End));
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        Sent.Add(Encoding.UTF8.GetString(buffer));
        return Task.CompletedTask;
    }

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;
    public override void Abort() => _state = WebSocketState.Aborted;
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
    public override void Dispose() { }
}
