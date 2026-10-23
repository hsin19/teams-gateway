using Microsoft.EntityFrameworkCore;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

/// <summary>重啟 / 睡眠補缺口的水位線：只有整段同步會推進，先到的單則新訊息不能讓缺口被跳過。</summary>
public class SyncWatermarkTests
{
    private const string Conv = "19:x@thread.v2";
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Window = Now.AddDays(-7);

    private static async Task AddConversationAsync(TestDb db, DateTimeOffset? syncedThrough)
    {
        await using var ctx = db.CreateDbContext();
        ctx.Conversations.Add(new Conversation { Id = Conv, Subscribed = true, SyncedThrough = syncedThrough, UpdatedAt = Now });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Gap_starts_at_watermark_even_if_a_newer_message_arrived_first()
    {
        // 停機一小時；重啟後 Trouter 先推來一則新的。舊做法拿「已存最新一則」當起點 → 這一小時被跳過
        using var db = new TestDb();
        await AddConversationAsync(db, Now.AddHours(-1));
        await using (var ctx = db.CreateDbContext())
        {
            ctx.MessageEvents.Add(new MessageEvent { Id = "1", ConversationId = Conv, ComposeTime = Now, RawJson = "{}" });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (since, isGap) = await Backfiller.ResolveSinceAsync(db, Conv, Window, TestContext.Current.CancellationToken);

        Assert.Equal(Now.AddHours(-1), since);
        Assert.True(isGap);
    }

    [Fact]
    public async Task Never_synced_fills_window_without_publishing()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, syncedThrough: null);

        var (since, isGap) = await Backfiller.ResolveSinceAsync(db, Conv, Window, TestContext.Current.CancellationToken);

        Assert.Equal(Window, since);
        Assert.False(isGap);
    }

    [Fact]
    public async Task Watermark_older_than_window_fills_window_without_publishing()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, Window.AddDays(-1));

        var (since, isGap) = await Backfiller.ResolveSinceAsync(db, Conv, Window, TestContext.Current.CancellationToken);

        Assert.Equal(Window, since);
        Assert.False(isGap);
    }

    [Fact]
    public async Task MarkSynced_only_moves_forward()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, Now);
        var ct = TestContext.Current.CancellationToken;

        await Backfiller.MarkSyncedAsync(db, Conv, Now.AddMinutes(-5), ct);
        await using (var ctx = db.CreateDbContext())
            Assert.Equal(Now, (await ctx.Conversations.SingleAsync(ct)).SyncedThrough);

        await Backfiller.MarkSyncedAsync(db, Conv, Now.AddMinutes(5), ct);
        await using (var ctx = db.CreateDbContext())
            Assert.Equal(Now.AddMinutes(5), (await ctx.Conversations.SingleAsync(ct)).SyncedThrough);
    }

    [Theory]
    [InlineData(null, true)]   // 從沒同步過
    [InlineData(-60, true)]    // 睡了一小時：輪詢只回看 15 分鐘，補不到
    [InlineData(-14, false)]
    [InlineData(-1, false)]
    public void Poll_backfills_when_watermark_is_beyond_lookback(int? minutesAgo, bool expected) =>
        Assert.Equal(expected, PollService.NeedsBackfill(minutesAgo is { } m ? Now.AddMinutes(m) : null, Now));
}
