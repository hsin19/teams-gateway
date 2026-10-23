using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>Teams 有人按 / 收回表情：thread 裡留言誰做了什麼，bot 在原訊息按 / 收回同一個 Slack 表情。</summary>
public class RelayReactionTests
{
    private const string MessageId = "1792640000000";

    private static ReactionDto Reaction(string emoji, bool added, int count, string? name = "Alice") =>
        new(MessageId, "19:x@thread.v2", emoji, "8:alice", name, added, DateTimeOffset.UtcNow, count);

    [Fact]
    public async Task Added_reaction_is_noted_in_thread_and_mirrored()
    {
        var slack = await RelayAsync(Reaction("1f440_eyes", added: true, count: 1));

        var reply = slack.Requests.Single(r => r.Method == "chat.postMessage");
        Assert.Equal("111.222", reply.Body.GetProperty("thread_ts").GetString());
        Assert.Equal("*Alice* reacted 👀", reply.Body.GetProperty("text").GetString());
        var react = slack.Requests.Single(r => r.Method == "reactions.add");
        Assert.Equal(("C1", "111.222", "eyes"),
            (react.Body.GetProperty("channel").GetString(), react.Body.GetProperty("timestamp").GetString(), react.Body.GetProperty("name").GetString()));
    }

    // 還有別人按著：只留言，bot 的表情不拿掉
    [Fact]
    public async Task Removed_reaction_keeps_mirror_while_others_still_react()
    {
        var slack = await RelayAsync(Reaction("heart", added: false, count: 1));

        Assert.Equal("*Alice* removed ❤️", slack.Requests.Single(r => r.Method == "chat.postMessage").Body.GetProperty("text").GetString());
        Assert.DoesNotContain(slack.Requests, r => r.Method.StartsWith("reactions.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Last_removal_takes_the_mirror_away()
    {
        var slack = await RelayAsync(Reaction("heart", added: false, count: 0));

        Assert.Equal("heart", slack.Requests.Single(r => r.Method == "reactions.remove").Body.GetProperty("name").GetString());
    }

    // 對不到 Slack 表情：留言照留（顯示原始代碼），不按原生表情
    [Fact]
    public async Task Unknown_emoji_is_noted_without_mirror()
    {
        var slack = await RelayAsync(Reaction("likeWithFaceMSER", added: true, count: 1, name: null));

        Assert.Equal("*Someone* reacted (likeWithFaceMSER)", slack.Requests.Single(r => r.Method == "chat.postMessage").Body.GetProperty("text").GetString());
        Assert.DoesNotContain(slack.Requests, r => r.Method.StartsWith("reactions.", StringComparison.Ordinal));
    }

    // 那則沒轉過：沒地方掛，什麼都不做
    [Fact]
    public async Task Reaction_on_unrelayed_message_is_ignored()
    {
        var slack = await RelayAsync(Reaction("heart", added: true, count: 1) with { MessageId = "999" });

        Assert.Empty(slack.Requests);
    }

    private static async Task<RecordingSlack> RelayAsync(ReactionDto reaction)
    {
        var slack = new RecordingSlack();
        var links = new MessageLinks();
        links.Remember(MessageId, "C1", "111.222", "*Bob*:\nhello", long.Parse(MessageId));
        var worker = new RelayWorker(
            new GatewayEventStream(new HttpClient(), NullLogger<GatewayEventStream>.Instance),
            new SlackClient(new HttpClient(slack) { BaseAddress = new Uri("https://slack.com/api/") }, NullLogger<SlackClient>.Instance),
            new GatewayMediaClient(new HttpClient(), NullLogger<GatewayMediaClient>.Instance),
            links,
            Options.Create(new RelayOptions()),
            NullLogger<RelayWorker>.Instance);

        await worker.RelayReactionAsync(reaction, TestContext.Current.CancellationToken);
        return slack;
    }
}
