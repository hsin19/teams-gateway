using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>
/// Teams 有人按 / 收回表情：thread 裡留言誰做了什麼，bot 在原訊息按 / 收回同一個 Slack 表情。
/// 留言夾著 gateway 的事件序號，relay 重啟後重送同一個事件時不再貼。
/// </summary>
public class RelayReactionTests
{
    private const string MessageId = "1792640000000";
    private const long Seq = 42;

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

    // 留言夾著事件序號，之後重送才認得出來
    [Fact]
    public async Task Note_carries_the_event_seq()
    {
        var slack = await RelayAsync(Reaction("heart", added: true, count: 1));

        var metadata = slack.Requests.Single(r => r.Method == "chat.postMessage").Body.GetProperty("metadata");
        Assert.Equal("teams_reaction", metadata.GetProperty("event_type").GetString());
        Assert.Equal(Seq, metadata.GetProperty("event_payload").GetProperty("seq").GetInt64());
    }

    // relay 重啟後從 afterMessage 續接會重送表情事件：thread 裡已經有這個序號的留言就不再貼，原生表情照按（冪等）
    [Fact]
    public async Task Replayed_reaction_already_noted_in_thread_is_not_noted_again()
    {
        var (worker, slack) = Worker();
        // conversations.replies 第一則是原訊息本身，後面才是回覆；seq 42 = Seq
        slack.Responses["conversations.replies"] = """
            {"ok":true,"messages":[
              {"ts":"111.222","text":"*Bob*:\nhello","metadata":{"event_type":"teams_relay","event_payload":{"teams_id":"1792640000000"}}},
              {"ts":"111.300","text":"*Alice* reacted ❤️","metadata":{"event_type":"teams_reaction","event_payload":{"seq":42}}}
            ]}
            """;

        await worker.RelayReactionAsync(Reaction("heart", added: true, count: 1), Seq, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(slack.Requests, r => r.Method == "chat.postMessage");
        Assert.Single(slack.Requests, r => r.Method == "reactions.add");
    }

    // 同一個 relay 行程裡重送（SSE 斷線重連）：記憶體裡記得貼過，也不再回 Slack 讀 thread
    [Fact]
    public async Task Same_event_twice_is_noted_once_and_thread_is_read_once()
    {
        var (worker, slack) = Worker();
        var ct = TestContext.Current.CancellationToken;

        await worker.RelayReactionAsync(Reaction("heart", added: true, count: 1), Seq, ct);
        await worker.RelayReactionAsync(Reaction("heart", added: true, count: 1), Seq, ct);

        Assert.Single(slack.Requests, r => r.Method == "chat.postMessage");
        Assert.Single(slack.Requests, r => r.Method == "conversations.replies");
    }

    // 沒有事件序號（SSE 沒帶 id）：沒得去重，照貼，也不用讀 thread
    [Fact]
    public async Task Reaction_without_seq_is_noted_without_reading_thread()
    {
        var slack = await RelayAsync(Reaction("heart", added: true, count: 1), seq: null);

        Assert.False(slack.Requests.Single(r => r.Method == "chat.postMessage").Body.TryGetProperty("metadata", out _));
        Assert.DoesNotContain(slack.Requests, r => r.Method == "conversations.replies");
    }

    private static async Task<RecordingSlack> RelayAsync(ReactionDto reaction, long? seq = Seq)
    {
        var (worker, slack) = Worker();
        await worker.RelayReactionAsync(reaction, seq, TestContext.Current.CancellationToken);
        return slack;
    }

    private static (RelayWorker Worker, RecordingSlack Slack) Worker()
    {
        var slack = new RecordingSlack();
        var links = new MessageLinks();
        links.Remember(MessageId, "C1", "111.222", "*Bob*:\nhello", long.Parse(MessageId));
        var worker = new RelayWorker(
            new SseEventSource(new GatewayEventStream(new HttpClient(), NullLogger<GatewayEventStream>.Instance), new MessageLinks(), NullLogger<SseEventSource>.Instance),
            new SlackClient(new HttpClient(slack) { BaseAddress = new Uri("https://slack.com/api/") }, NullLogger<SlackClient>.Instance),
            new GatewayMediaClient(new HttpClient(), NullLogger<GatewayMediaClient>.Instance),
            links,
            Options.Create(new RelayOptions()),
            NullLogger<RelayWorker>.Instance);
        return (worker, slack);
    }
}
