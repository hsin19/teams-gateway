using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

public class TrouterReceiveTests
{
    [Fact]
    public async Task Multibyte_character_split_across_fragments_survives()
    {
        // 「中」是 3 bytes：在它中間切段，各段各自解碼會變成 U+FFFD
        const string text = """3:::{"body":"{\"content\":\"中文訊息\"}"}""";
        var bytes = Encoding.UTF8.GetBytes(text);
        var split = Encoding.UTF8.GetByteCount(text[..text.IndexOf('中')]) + 1;
        var ws = new FragmentedWebSocket([bytes[..split], bytes[split..]]);

        var received = await TrouterClient.ReceiveTextAsync(ws, new byte[64], new ArrayBufferWriter<byte>(), TestContext.Current.CancellationToken);

        Assert.Equal(text, received);
    }

    [Fact]
    public async Task Close_returns_null()
    {
        var ws = new FragmentedWebSocket([]);
        Assert.Null(await TrouterClient.ReceiveTextAsync(ws, new byte[64], new ArrayBufferWriter<byte>(), TestContext.Current.CancellationToken));
    }

    /// <summary>依序吐出給定的片段（最後一段標 EndOfMessage），吐完就回 Close。</summary>
    private sealed class FragmentedWebSocket(byte[][] fragments) : WebSocket
    {
        private int _next;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_next >= fragments.Length)
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            var fragment = fragments[_next++];
            fragment.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(fragment.Length, WebSocketMessageType.Text, _next == fragments.Length));
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
