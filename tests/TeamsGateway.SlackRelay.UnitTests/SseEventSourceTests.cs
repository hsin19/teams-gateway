using System.Net;
using System.Text;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>SSE source（ADR 0006）：游標、續接、重連都在這裡，worker 拿到的是一條不會斷的串流。</summary>
public class SseEventSourceTests
{
    // 剛啟動沒有序號：從 Slack 上最後一則轉過的訊息之後續接
    [Fact]
    public async Task First_connect_resumes_after_the_newest_relayed_message()
    {
        var gateway = new ScriptedGateway(Event("7", "100"));
        var links = new MessageLinks();
        links.Remember("T1", "C1", "1792000000.000100", "*Bob*:\nhi", 1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await foreach (var _ in Source(gateway, links).ReadAsync(["19:x@thread.v2"], cts.Token))
            break;

        var first = gateway.Requests.Single();
        Assert.Equal("T1", first.AfterMessage);
        Assert.Null(first.LastEventId);
        Assert.Equal("19:x@thread.v2", first.Chats);
    }

    // 斷線重連帶的是「處理完的最後一筆」：呼叫端來拿下一筆時，上一筆才算處理完
    [Fact]
    public async Task Reconnects_with_the_last_processed_event_id()
    {
        var gateway = new ScriptedGateway(Event("5", "100") + Event("6", "101"), Event("7", "102"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var ids = new List<string?>();
        await foreach (var evt in Source(gateway, new MessageLinks()).ReadAsync(null, cts.Token))
        {
            ids.Add(evt.Id);
            if (ids.Count == 3)
                break;
        }

        Assert.Equal(["5", "6", "7"], ids);
        var reconnect = gateway.Requests[1];
        Assert.Equal("6", reconnect.LastEventId);
        Assert.Null(reconnect.AfterMessage); // 有序號就不再用 afterMessage
    }

    private static SseEventSource Source(ScriptedGateway gateway, MessageLinks links) =>
        new(new GatewayEventStream(new HttpClient(gateway) { BaseAddress = new Uri("http://localhost") }, NullLogger<GatewayEventStream>.Instance),
            links, NullLogger<SseEventSource>.Instance) { FirstBackoff = TimeSpan.FromMilliseconds(10) };

    private static string Event(string id, string messageId) =>
        $"id: {id}\nevent: message\ndata: {{\"id\":\"{messageId}\",\"conversationId\":\"19:x@thread.v2\",\"sequenceId\":1,\"time\":\"2026-10-21T00:00:00+00:00\"}}\n\n";

    /// <summary>假的 gateway：第 n 次連線回第 n 段 body，然後關掉連線；記下每次帶的續接參數。</summary>
    private sealed class ScriptedGateway(params string[] bodies) : HttpMessageHandler
    {
        public List<(string? LastEventId, string? AfterMessage, string? Chats)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            Requests.Add((request.Headers.TryGetValues("Last-Event-ID", out var ids) ? ids.Single() : null, query["afterMessage"], query["chats"]));
            var body = Requests.Count <= bodies.Length ? bodies[Requests.Count - 1] : "";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
        }
    }
}
