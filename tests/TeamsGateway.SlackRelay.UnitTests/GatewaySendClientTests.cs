using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>Gateway send recovery searches the complete message history before deciding that a retry is safe.</summary>
public class GatewaySendClientTests
{
    [Fact]
    public async Task Find_sent_follows_cursors_until_the_send_is_found()
    {
        var handler = new PagedGateway();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test") };
        var client = new GatewaySendClient(http, NullLogger<GatewaySendClient>.Instance);

        var found = await client.FindSentAsync("chat", "client-id", TestContext.Current.CancellationToken);

        Assert.Equal("teams-id", found);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("limit=200", handler.Requests[0]);
        Assert.Contains("cursor=https%3A%2F%2Fteams.microsoft.com%2Fpage%3Ftoken%3Da%252Bb", handler.Requests[1]);
    }

    [Fact]
    public async Task Find_sent_rejects_a_repeated_cursor_instead_of_claiming_the_send_is_missing()
    {
        var handler = new PagedGateway(repeatCursor: true);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test") };
        var client = new GatewaySendClient(http, NullLogger<GatewaySendClient>.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.FindSentAsync("chat", "client-id", TestContext.Current.CancellationToken));
    }

    private sealed class PagedGateway(bool repeatCursor = false) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            var json = Requests.Count == 1 || repeatCursor
                ? """{"messages":[],"nextCursor":"https://teams.microsoft.com/page?token=a%2Bb"}"""
                : """{"messages":[{"id":"teams-id","conversationId":"chat","sequenceId":1,"time":"2026-10-28T00:00:00Z","clientMessageId":"client-id"}],"nextCursor":null}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
