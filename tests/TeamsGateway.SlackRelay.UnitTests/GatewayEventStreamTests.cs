using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsGateway.Contracts;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

public class GatewayEventStreamTests
{
    // 兩則 message 事件，中間夾一個非 message 事件、結尾一則壞 JSON
    private const string SseBody =
        "id: 100\n" +
        "event: message\n" +
        "data: {\"id\":\"100\",\"conversationId\":\"19:x@thread.v2\",\"from\":\"8:me\",\"fromName\":\"Me\",\"type\":\"Text\",\"content\":\"hi\",\"sequenceId\":1,\"time\":\"2026-10-21T00:00:00+00:00\"}\n" +
        "\n" +
        "event: presence\n" +
        "data: {\"ignored\":true}\n" +
        "\n" +
        "id: 101\n" +
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

        var messages = new List<MessageDto>();
        await foreach (var message in stream.ConnectAsync(null, null, TestContext.Current.CancellationToken))
            messages.Add(message);

        Assert.Equal(["100", "101"], messages.Select(m => m.Id));
        Assert.Equal("hi", messages[0].Content);
        Assert.Equal("Me", messages[0].FromName);
        Assert.Equal("second", messages[1].Content);
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
