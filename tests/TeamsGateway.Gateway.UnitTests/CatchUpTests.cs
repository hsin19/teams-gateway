using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Apis;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

/// <summary>從內部事件 log 補收：照事件序號，不是訊息 id。</summary>
public class CatchUpTests
{
    private const string Conv = "19:x@thread.v2";

    private static MessageDto Msg(string id, long version, string content) =>
        new(id, Conv, "8:me", "me", "Text", content, 1, DateTimeOffset.UtcNow, version);

    /// <summary>A、B 兩則新訊息 + A 被編輯，都 publish → 序號 1、2、3。</summary>
    private static async Task SeedAsync(TestDb db)
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var ctx = db.CreateDbContext())
        {
            ctx.Conversations.Add(new Conversation { Id = Conv, Subscribed = true, UpdatedAt = DateTimeOffset.UtcNow });
            await ctx.SaveChangesAsync(ct);
        }
        var ingestor = new MessageIngestor(db, new GatewayEventBus());
        await ingestor.UpsertAsync(Msg("100", 100, "A"), "{}", publish: true, ct);
        await ingestor.UpsertAsync(Msg("200", 200, "B"), "{}", publish: true, ct);
        await ingestor.UpsertAsync(Msg("100", 300, "A 改過"), "{}", publish: true, ct);
    }

    [Fact]
    public async Task Replays_events_after_the_sequence()
    {
        using var db = new TestDb();
        await SeedAsync(db);

        var missed = await EventLog.ReadAfterAsync(db, 1, limit: 500, TestContext.Current.CancellationToken);

        Assert.Equal([2L, 3L], missed.Select(e => e.Seq));
        Assert.Equal(["200", "100"], missed.Select(e => e.MessageId)); // 編輯是新事件，排在後面
    }

    [Fact]
    public async Task Edit_as_last_event_replays_nothing_older()
    {
        // 舊做法用訊息 id 當錨點：最後收到的是「舊訊息的編輯」時會退回那則的時間、把之後的全部重送
        using var db = new TestDb();
        await SeedAsync(db);

        Assert.Empty(await EventLog.ReadAfterAsync(db, 3, limit: 500, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Limit_bounds_one_batch()
    {
        using var db = new TestDb();
        await SeedAsync(db);

        var batch = await EventLog.ReadAfterAsync(db, 0, limit: 2, TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L], batch.Select(e => e.Seq));
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData(null, 3)]            // 沒帶 → 從現在開始
    [InlineData("", 3)]
    [InlineData("garbage", 3)]
    [InlineData("-1", 3)]
    [InlineData("1792527319389", 3)] // 舊 client 帶的訊息 id：比最大序號大，不能當游標（不然之後的事件全被當成送過）
    public async Task Start_cursor_falls_back_to_now_unless_valid(string? lastEventId, long expected)
    {
        using var db = new TestDb();
        await SeedAsync(db);

        Assert.Equal(expected, await EventsApi.StartCursorAsync(db, lastEventId, afterMessage: null, TestContext.Current.CancellationToken));
    }

    // 訂閱者重啟：沒有序號，但知道最後轉出去的是哪則 → 從那則第一次發出之後補（A 的編輯、B 都會重送）
    [Theory]
    [InlineData(null, "100", 1)]
    [InlineData(null, "200", 2)]
    [InlineData(null, "unknown", 3)] // 找不到（太舊被清掉了）→ 從現在開始
    [InlineData("2", "100", 2)]      // 有合法序號就用序號
    public async Task Start_cursor_can_resume_after_a_message(string? lastEventId, string afterMessage, long expected)
    {
        using var db = new TestDb();
        await SeedAsync(db);

        Assert.Equal(expected, await EventsApi.StartCursorAsync(db, lastEventId, afterMessage, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Start_cursor_is_zero_on_empty_log()
    {
        using var db = new TestDb();
        Assert.Equal(0, await EventsApi.StartCursorAsync(db, null, afterMessage: null, TestContext.Current.CancellationToken));
    }
}
