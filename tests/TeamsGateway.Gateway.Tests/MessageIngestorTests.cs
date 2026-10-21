using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.Tests;

public class MessageIngestorTests
{
    private static MessageDto Msg(string id) =>
        new(id, "19:x@thread.v2", "8:me", "me", "Text", "hi", 1, DateTimeOffset.UtcNow);

    private static async Task AddConversationAsync(TestDb db, bool subscribed)
    {
        await using var ctx = db.CreateDbContext();
        ctx.Conversations.Add(new Conversation { Id = "19:x@thread.v2", Subscribed = subscribed, UpdatedAt = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Store_is_idempotent_by_message_id()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());

        Assert.True(await ingestor.StoreAsync(Msg("m1"), "{}", publish: false, default));
        Assert.False(await ingestor.StoreAsync(Msg("m1"), "{}", publish: false, default)); // 重送

        await using var ctx = db.CreateDbContext();
        Assert.Single(ctx.MessageEvents);
    }

    [Fact]
    public async Task IsSubscribed_reflects_conversation_flag()
    {
        using var db = new TestDb();
        var ingestor = new MessageIngestor(db, new GatewayEventBus());

        Assert.False(await ingestor.IsSubscribedAsync("19:x@thread.v2", default)); // 還沒有這個對話

        await AddConversationAsync(db, subscribed: false);
        Assert.False(await ingestor.IsSubscribedAsync("19:x@thread.v2", default)); // 有但沒訂閱
    }

    [Fact]
    public async Task IsSubscribed_true_when_subscribed()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());

        Assert.True(await ingestor.IsSubscribedAsync("19:x@thread.v2", default));
    }
}
