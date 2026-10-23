using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

public class GatewayEventStreamTests
{
    // 兩則 message 事件，中間夾一個非 message 事件、結尾一則壞 JSON
    private const string SseBody =
        "id: 7\n" +
        "event: message\n" +
        "data: {\"id\":\"100\",\"conversationId\":\"19:x@thread.v2\",\"from\":\"8:me\",\"fromName\":\"Me\",\"type\":\"Text\",\"content\":\"hi\",\"sequenceId\":1,\"time\":\"2026-10-21T00:00:00+00:00\"}\n" +
        "\n" +
        "event: presence\n" +
        "data: {\"ignored\":true}\n" +
        "\n" +
        "id: 8\n" +
        "event: message\n" +
        "data: {\"id\":\"101\",\"conversationId\":\"19:x@thread.v2\",\"content\":\"second\",\"sequenceId\":2,\"time\":\"2026-10-21T00:00:01+00:00\"}\n" +
        "\n" +
        "event: message\n" +
        "data: not-json\n" +
        "\n";

    [Fact]
    public async Task Parses_message_events_skips_others_and_bad_json()
    {
        var http = new HttpClient(new StubHandler(SseBody)) { BaseAddress = new Uri("http://localhost") };
        var stream = new GatewayEventStream(http, NullLogger<GatewayEventStream>.Instance);

        var events = new List<StreamEvent>();
        await foreach (var evt in stream.ConnectAsync(null, null, TestContext.Current.CancellationToken))
            events.Add(evt);
        var messages = events.Select(e => e.Message).ToList();

        Assert.Equal(["7", "8"], events.Select(e => e.Id)); // SSE id 是事件序號，續接用它
        Assert.Equal(["100", "101"], messages.Select(m => m.Id));
        Assert.Equal("hi", messages[0].Content);
        Assert.Equal("Me", messages[0].FromName);
        Assert.Equal("second", messages[1].Content);
    }

    [Fact]
    public async Task Silent_connection_times_out_instead_of_hanging()
    {
        // half-open 的 TCP：沒資料也沒斷線。沒有逾時的話 ReadLineAsync 會永遠卡住、relay 不會重連
        var http = new HttpClient(new SilentHandler()) { BaseAddress = new Uri("http://localhost") };
        var stream = new GatewayEventStream(http, NullLogger<GatewayEventStream>.Instance, idleTimeout: TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in stream.ConnectAsync(null, null, TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task Slow_consumer_does_not_trip_the_idle_timeout()
    {
        // 處理一則訊息（例如傳圖到 Slack）比逾時還久，不能被當成 gateway 沒動靜
        var http = new HttpClient(new ChunkedHandler(SseBody.Split("\n\n").Select(e => e + "\n\n").ToArray()))
        {
            BaseAddress = new Uri("http://localhost"),
        };
        var stream = new GatewayEventStream(http, NullLogger<GatewayEventStream>.Instance, idleTimeout: TimeSpan.FromMilliseconds(200));

        var ids = new List<string?>();
        await foreach (var evt in stream.ConnectAsync(null, null, TestContext.Current.CancellationToken))
        {
            ids.Add(evt.Id);
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["7", "8"], ids);
    }

    /// <summary>每次 ReadAsync 吐一段（每個 SSE 事件一段），吐完就結束；會檢查取消。</summary>
    private sealed class ChunkedHandler(string[] chunks) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ChunkStream(chunks.Select(Encoding.UTF8.GetBytes))),
            });
    }

    private sealed class ChunkStream(IEnumerable<byte[]> chunks) : NeverStream
    {
        private readonly Queue<byte[]> _chunks = new(chunks);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_chunks.TryDequeue(out var chunk))
                return ValueTask.FromResult(0);
            chunk.CopyTo(buffer);
            return ValueTask.FromResult(chunk.Length);
        }
    }

    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverStream()) });
    }

    /// <summary>讀了就一直等，直到被取消。</summary>
    private class NeverStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
    }
}
