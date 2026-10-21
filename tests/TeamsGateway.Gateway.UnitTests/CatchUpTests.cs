using TeamsGateway.Gateway.Apis;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.UnitTests;

public class CatchUpTests
{
    private static async Task SeedAsync(TestDb db)
    {
        await using var ctx = db.CreateDbContext();
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ctx.Conversations.Add(new Conversation { Id = "19:x@thread.v2", UpdatedAt = baseTime });
        foreach (var (id, offset) in new[] { ("A", 0), ("B", 1), ("C", 2) })
        {
            ctx.MessageEvents.Add(new MessageEvent
            {
                Id = id,
                ConversationId = "19:x@thread.v2",
                Content = id,
                ReceivedAt = baseTime.AddSeconds(offset),
                ComposeTime = baseTime.AddSeconds(offset),
                RawJson = "{}",
            });
        }
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Replays_messages_after_the_anchor()
    {
        using var db = new TestDb();
        await SeedAsync(db);

        var missed = await EventsApi.CatchUpAsync(db, "A", TestContext.Current.CancellationToken);

        Assert.Equal(["B", "C"], missed.Select(m => m.Id));
    }

    [Fact]
    public async Task Newest_anchor_replays_nothing()
    {
        using var db = new TestDb();
        await SeedAsync(db);

        Assert.Empty(await EventsApi.CatchUpAsync(db, "C", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_anchor_replays_nothing()
    {
        using var db = new TestDb();
        await SeedAsync(db);

        Assert.Empty(await EventsApi.CatchUpAsync(db, "does-not-exist", TestContext.Current.CancellationToken));
    }
}
