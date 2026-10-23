using Microsoft.EntityFrameworkCore;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

public class MessageIngestorTests
{
    private static MessageDto Msg(string id, long version, string? content = "hi", bool edited = false, bool deleted = false) =>
        new(id, "19:x@thread.v2", "8:me", "me", "Text", content, 1, DateTimeOffset.UtcNow, version, edited, deleted);

    private static async Task AddConversationAsync(TestDb db, bool subscribed)
    {
        await using var ctx = db.CreateDbContext();
        ctx.Conversations.Add(new Conversation { Id = "19:x@thread.v2", Subscribed = subscribed, UpdatedAt = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Upsert_inserts_then_dedupes_same_version()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());

        Assert.Equal(IngestResult.Inserted, await ingestor.UpsertAsync(Msg("100", version: 100), "{}", false, TestContext.Current.CancellationToken));
        Assert.Equal(IngestResult.Unchanged, await ingestor.UpsertAsync(Msg("100", version: 100), "{}", false, TestContext.Current.CancellationToken));

        await using var ctx = db.CreateDbContext();
        Assert.Single(ctx.MessageEvents);
    }

    [Fact]
    public async Task Higher_version_updates_content_and_marks_edited()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());

        await ingestor.UpsertAsync(Msg("100", version: 100, content: "原文"), "{}", false, TestContext.Current.CancellationToken);
        var result = await ingestor.UpsertAsync(Msg("100", version: 200, content: "改過", edited: true), "{}", false, TestContext.Current.CancellationToken);

        Assert.Equal(IngestResult.Updated, result);
        await using var ctx = db.CreateDbContext();
        var row = ctx.MessageEvents.Single();
        Assert.Equal("改過", row.Content);
        Assert.True(row.Edited);
        Assert.Equal(200, row.Version);

        // 舊版本留在 revisions，看得到被改掉前的內容
        var revision = ctx.MessageRevisions.Single();
        Assert.Equal("原文", revision.Content);
        Assert.Equal(100, revision.Version);
    }

    [Fact]
    public async Task Delete_marker_sets_deleted()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());

        await ingestor.UpsertAsync(Msg("100", version: 100), "{}", false, TestContext.Current.CancellationToken);
        await ingestor.UpsertAsync(Msg("100", version: 300, content: null, deleted: true), "{}", false, TestContext.Current.CancellationToken);

        await using var ctx = db.CreateDbContext();
        Assert.True(ctx.MessageEvents.Single().Deleted);
    }

    [Fact]
    public async Task Publish_records_event_and_sends_its_sequence()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var bus = new GatewayEventBus();
        using var sub = bus.Subscribe();
        var ingestor = new MessageIngestor(db, bus);
        var ct = TestContext.Current.CancellationToken;

        await ingestor.UpsertAsync(Msg("100", version: 100), "{}", publish: true, ct);
        await ingestor.UpsertAsync(Msg("100", version: 200, content: "改過", edited: true), "{}", publish: true, ct);

        Assert.True(sub.Reader.TryRead(out var inserted));
        Assert.True(sub.Reader.TryRead(out var edited));
        Assert.True(edited!.Seq > inserted!.Seq); // 編輯同訊息 id，但是新的事件序號
        await using var ctx = db.CreateDbContext();
        Assert.Equal([inserted.Seq, edited.Seq], ctx.PublishedEvents.OrderBy(e => e.Seq).Select(e => e.Seq));
    }

    [Fact]
    public async Task Concurrent_upserts_of_the_same_message_insert_once()
    {
        // Trouter 推來的同時輪詢也抓到同一則：先查再插不能撞主鍵
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());
        var ct = TestContext.Current.CancellationToken;

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => ingestor.UpsertAsync(Msg("100", version: 100), "{}", publish: true, ct), ct)));

        Assert.Single(results, r => r == IngestResult.Inserted);
        await using var ctx = db.CreateDbContext();
        Assert.Single(ctx.PublishedEvents);
    }

    [Fact]
    public async Task Store_only_backfill_records_no_event()
    {
        // 首次訂閱的歷史回補只存不發：也不能進事件流水帳，不然重連補收會把歷史洗給訂閱者
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());

        await ingestor.UpsertAsync(Msg("100", version: 100), "{}", publish: false, TestContext.Current.CancellationToken);

        await using var ctx = db.CreateDbContext();
        Assert.Empty(ctx.PublishedEvents);
    }

    // 時間存 Unix 毫秒（INTEGER）：比較 / 排序是整數運算，讀回來是同一個時間點
    [Fact]
    public async Task Times_are_stored_as_unix_milliseconds()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var composed = new DateTimeOffset(2026, 10, 22, 12, 34, 56, 789, TimeSpan.FromHours(8));
        var ct = TestContext.Current.CancellationToken;
        await new MessageIngestor(db, new GatewayEventBus())
            .UpsertAsync(Msg("100", version: 100) with { Time = composed }, "{}", false, ct);

        await using var ctx = db.CreateDbContext();
        var connection = ctx.Database.GetDbConnection();
        await ctx.Database.OpenConnectionAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT typeof(ComposeTime), ComposeTime FROM MessageEvents";
            await using var reader = await command.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal("integer", reader.GetString(0));
            Assert.Equal(composed.ToUnixTimeMilliseconds(), reader.GetInt64(1));
        }
        Assert.Equal(composed, ctx.MessageEvents.Single().ComposeTime); // 同一個時間點（讀回來是 UTC）
    }

    [Fact]
    public async Task IsSubscribed_reflects_conversation_flag()
    {
        using var db = new TestDb();
        var ingestor = new MessageIngestor(db, new GatewayEventBus());

        Assert.False(await ingestor.IsSubscribedAsync("19:x@thread.v2", TestContext.Current.CancellationToken));
        await AddConversationAsync(db, subscribed: false);
        Assert.False(await ingestor.IsSubscribedAsync("19:x@thread.v2", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsSubscribed_true_when_subscribed()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());

        Assert.True(await ingestor.IsSubscribedAsync("19:x@thread.v2", TestContext.Current.CancellationToken));
    }
}
