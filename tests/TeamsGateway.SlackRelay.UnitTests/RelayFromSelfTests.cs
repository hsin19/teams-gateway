using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>
/// gateway 綁的帳號發的訊息（從 Slack、別的 API key、MCP 發的）推回來轉貼時，metadata 標 from_self，
/// 讀頻道的 agent 才認得出是自己這邊送出去的話。
/// </summary>
public class RelayFromSelfTests
{
    private const string Conv = "19:x@thread.v2";
    private const string Account = "8:live:gateway";
    private static readonly Dictionary<string, string> Routes = new() { [Conv] = "C1" };

    private static (RelayWorker Worker, RecordingSlack Slack, MessageLinks Links) Build(FakeGateway gateway)
    {
        var slack = new RecordingSlack();
        var links = new MessageLinks();
        var worker = new RelayWorker(
            new SseEventSource(new GatewayEventStream(new HttpClient(), NullLogger<GatewayEventStream>.Instance), new MessageLinks(), NullLogger<SseEventSource>.Instance),
            RelayFakes.Slack(slack),
            new GatewayMediaClient(new HttpClient(), NullLogger<GatewayMediaClient>.Instance),
            links,
            RelayFakes.Sends(slack, links),
            RelayFakes.Bot(slack),
            RelayFakes.Account(gateway),
            Options.Create(new RelayOptions()),
            NullLogger<RelayWorker>.Instance);
        return (worker, slack, links);
    }

    private static FakeGateway GatewayKnowing(string? account) =>
        new() { Responses = { ["GET /v1/status"] = (HttpStatusCode.OK, JsonSerializer.Serialize(new { teamsAuth = "ok", accountMri = account })) } };

    private static MessageDto Message(string id, string from, string name, long version = 0, bool edited = false) =>
        new(id, Conv, from, name, "Text", "hello", 1, DateTimeOffset.UtcNow, version == 0 ? long.Parse(id) : version, Edited: edited);

    private static JsonElement Payload(RecordingSlack slack, string method, int index = 0) =>
        slack.Requests.Where(r => r.Method == method).ElementAt(index).Body.GetProperty("metadata").GetProperty("event_payload");

    [Fact]
    public async Task Messages_from_the_gateway_account_are_marked_and_others_are_not()
    {
        var (worker, slack, _) = Build(GatewayKnowing(Account));
        var ct = TestContext.Current.CancellationToken;

        await worker.RelayWithRetryAsync(Message("1792640000000", Account, "teams-gateway"), Routes, null, ct);
        await worker.RelayWithRetryAsync(Message("1792640001000", "8:live:bob", "Bob"), Routes, null, ct);

        Assert.True(Payload(slack, "chat.postMessage").GetProperty("from_self").GetBoolean());
        Assert.False(Payload(slack, "chat.postMessage", 1).TryGetProperty("from_self", out _));
    }

    // 原地更新時 metadata 整份重寫，不帶的話標記就掉了
    [Fact]
    public async Task Edits_of_the_gateway_account_messages_stay_marked()
    {
        var (worker, slack, links) = Build(GatewayKnowing(Account));
        links.Remember("1792640000000", "C1", "111.222", "*teams-gateway*:\nhello", 1792640000000);

        await worker.RelayWithRetryAsync(
            Message("1792640000000", Account, "teams-gateway", version: 1792640480711, edited: true), Routes, null, TestContext.Current.CancellationToken);

        Assert.True(Payload(slack, "chat.update").GetProperty("from_self").GetBoolean());
    }

    // gateway 剛啟動還沒換到 token 時不知道帳號：先不標，之後再問；知道了就記住，不再每則都問
    [Fact]
    public async Task Account_is_asked_again_until_the_gateway_knows_it_then_remembered()
    {
        var known = false;
        var gateway = new FakeGateway
        {
            RespondAsync = (_, _) => Task.FromResult<(HttpStatusCode, string)?>(
                (HttpStatusCode.OK, JsonSerializer.Serialize(new { teamsAuth = known ? "ok" : "failed", accountMri = known ? Account : null }))),
        };
        var (worker, slack, _) = Build(gateway);
        var ct = TestContext.Current.CancellationToken;

        await worker.RelayWithRetryAsync(Message("1792640000000", Account, "teams-gateway"), Routes, null, ct);
        known = true;
        await worker.RelayWithRetryAsync(Message("1792640001000", Account, "teams-gateway"), Routes, null, ct);
        await worker.RelayWithRetryAsync(Message("1792640002000", Account, "teams-gateway"), Routes, null, ct);

        Assert.False(Payload(slack, "chat.postMessage").TryGetProperty("from_self", out _));
        Assert.True(Payload(slack, "chat.postMessage", 1).GetProperty("from_self").GetBoolean());
        Assert.True(Payload(slack, "chat.postMessage", 2).GetProperty("from_self").GetBoolean());
        Assert.Equal(2, gateway.Requests.Count(r => r.Path == "/v1/status"));
    }
}
