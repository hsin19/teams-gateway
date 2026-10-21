using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.Tests;

public class MessageIngestorTests
{
    private static MessageDto Msg(string id, long version, string? content = "hi", bool edited = false, bool deleted = false) =>
        new(id, "19:x@thread.v2", "8:me", "me", "Text", content, 1, DateTimeOffset.UtcNow, version, edited, deleted);

    private static async Task AddConversationAsync(TestDb db, bool subscribed)
    {
        await using var ctx = db.CreateDbContext();
        ctx.Conversations.Add(new Conversation { Id = "19:x@thread.v2", Subscribed = subscribed, UpdatedAt = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Upsert_inserts_then_dedupes_same_version()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());

        Assert.Equal(IngestResult.Inserted, await ingestor.UpsertAsync(Msg("100", version: 100), "{}", false, default));
        Assert.Equal(IngestResult.Unchanged, await ingestor.UpsertAsync(Msg("100", version: 100), "{}", false, default));

        await using var ctx = db.CreateDbContext();
        Assert.Single(ctx.MessageEvents);
    }

    [Fact]
    public async Task Higher_version_updates_content_and_marks_edited()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());

        await ingestor.UpsertAsync(Msg("100", version: 100, content: "原文"), "{}", false, default);
        var result = await ingestor.UpsertAsync(Msg("100", version: 200, content: "改過", edited: true), "{}", false, default);

        Assert.Equal(IngestResult.Updated, result);
        await using var ctx = db.CreateDbContext();
        var row = ctx.MessageEvents.Single();
        Assert.Equal("改過", row.Content);
        Assert.True(row.Edited);
        Assert.Equal(200, row.Version);
    }

    [Fact]
    public async Task Delete_marker_sets_deleted()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());

        await ingestor.UpsertAsync(Msg("100", version: 100), "{}", false, default);
        await ingestor.UpsertAsync(Msg("100", version: 300, content: null, deleted: true), "{}", false, default);

        await using var ctx = db.CreateDbContext();
        Assert.True(ctx.MessageEvents.Single().Deleted);
    }

    [Fact]
    public async Task IsSubscribed_reflects_conversation_flag()
    {
        using var db = new TestDb();
        var ingestor = new MessageIngestor(db, new GatewayEventBus());

        Assert.False(await ingestor.IsSubscribedAsync("19:x@thread.v2", default));
        await AddConversationAsync(db, subscribed: false);
        Assert.False(await ingestor.IsSubscribedAsync("19:x@thread.v2", default));
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
