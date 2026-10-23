using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>Teams 刪掉一則：Slack 那則原地改成只剩刪除註記，誰說的、原本寫什麼留在 thread 裡。</summary>
public class RelayDeleteTests
{
    private const string Conv = "19:x@thread.v2";

    [Fact]
    public async Task Deleted_message_becomes_a_bare_note_and_original_goes_to_thread()
    {
        var slack = new RecordingSlack();
        var links = new MessageLinks();
        links.Remember("1792640000000", "C1", "111.222", "*Alice*:\nhello", 1792640000000);
        var worker = new RelayWorker(
            new GatewayEventStream(new HttpClient(), NullLogger<GatewayEventStream>.Instance),
            new SlackClient(new HttpClient(slack) { BaseAddress = new Uri("https://slack.com/api/") }, NullLogger<SlackClient>.Instance),
            new GatewayMediaClient(new HttpClient(), NullLogger<GatewayMediaClient>.Instance),
            links,
            Options.Create(new RelayOptions()),
            NullLogger<RelayWorker>.Instance);
        var deleted = new MessageDto("1792640000000", Conv, "8:alice", "Alice", "RichText/Html", "", 1, DateTimeOffset.UtcNow, 1792640480711, Deleted: true);

        await worker.RelayWithRetryAsync(deleted, new Dictionary<string, string> { [Conv] = "C1" }, null, TestContext.Current.CancellationToken);

        var update = slack.Requests.Single(r => r.Method == "chat.update");
        Assert.Equal("_(message deleted)_", update.Body.GetProperty("text").GetString());
        var reply = slack.Requests.Single(r => r.Method == "chat.postMessage");
        Assert.Equal("111.222", reply.Body.GetProperty("thread_ts").GetString());
        Assert.Contains("*Alice*:\nhello", reply.Body.GetProperty("text").GetString());
    }
}
