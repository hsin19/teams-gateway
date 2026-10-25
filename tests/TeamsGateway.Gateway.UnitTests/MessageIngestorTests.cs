using System.Text.Json;
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

    // 有人按表情：Teams 讓 version 往上跳，但內容沒變——不是編輯：不留 revision、不發給訂閱者
    [Fact]
    public async Task Reaction_only_version_bump_is_not_an_edit()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var events = new NewEvents(db);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());
        var ct = TestContext.Current.CancellationToken;

        await ingestor.UpsertAsync(Msg("100", version: 100, content: "hi"), "{}", publish: true, ct);
        var reacted = await ingestor.UpsertAsync(Msg("100", version: 200, content: "hi"), "{}", publish: true, ct);

        Assert.Equal(IngestResult.Unchanged, reacted);
        Assert.Single(events.Take()); // 只有新訊息那一則，表情那次沒發
        await using (var ctx = db.CreateDbContext())
        {
            Assert.Empty(ctx.MessageRevisions);
            Assert.Equal(200, ctx.MessageEvents.Single().Version); // 版本有跟上
        }

        // 之後真的編輯：照常留 revision（被取代的是表情之後的那一版）並發出
        Assert.Equal(IngestResult.Updated, await ingestor.UpsertAsync(Msg("100", version: 300, content: "改過", edited: true), "{}", publish: true, ct));
        await using (var ctx = db.CreateDbContext())
            Assert.Equal(200, ctx.MessageRevisions.Single().Version);
        Assert.Equal("改過", MessageOf(Assert.Single(events.Take())).Content);
    }

    // 按 / 收回表情：發 reaction 事件（不是編輯），帶誰、什麼表情、還剩幾個人
    [Fact]
    public async Task Reactions_are_published_as_reaction_events()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var events = new NewEvents(db);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());
        var ct = TestContext.Current.CancellationToken;
        await ingestor.UpsertAsync(Msg("50", version: 50) with { From = "8:alice", FromName = "Alice" }, "{}", publish: false, ct); // Alice 講過話
        await ingestor.UpsertAsync(Msg("100", version: 100), "{}", publish: true, ct);
        Assert.Single(events.Take());

        const string heart = """{"properties":{"emotions":[{"key":"heart","users":[{"mri":"8:alice","time":1792640000000}]}]}}""";
        Assert.Equal(IngestResult.Unchanged, await ingestor.UpsertAsync(Msg("100", version: 200), heart, publish: true, ct));

        var added = Assert.Single(events.Take());
        Assert.Equal(EventKinds.Reaction, added.Kind);
        Assert.Equal(
            new ReactionDto("100", "19:x@thread.v2", "heart", "8:alice", "Alice", Added: true, DateTimeOffset.FromUnixTimeMilliseconds(1792640000000), Count: 1),
            ReactionOf(added));

        // Alice 收回 ❤️、Bob 按 👀（Bob 沒講過話，名字查不到）
        const string eyes = """{"properties":{"emotions":"[{\"key\":\"1f440_eyes\",\"users\":[{\"mri\":\"8:bob\",\"time\":1792640100000}]}]"}}""";
        await ingestor.UpsertAsync(Msg("100", version: 300), eyes, publish: true, ct);

        var changes = events.Take().Select(ReactionOf).ToList();
        Assert.Equal(
            [("1f440_eyes", "8:bob", (string?)null, true, 1), ("heart", "8:alice", "Alice", false, 0)],
            changes.Select(c => (c.Emoji, c.UserMri, c.UserName, c.Added, c.Count)).OrderBy(c => c.Emoji));
        await using var ctx = db.CreateDbContext();
        Assert.Empty(ctx.MessageRevisions); // 表情不是編輯
        Assert.Equal(3, ctx.PublishedEvents.Count(e => e.Kind == "reaction"));
    }

    // 晚到的訊息（例如電腦睡著時發的，醒來才被輪詢補到）第一次看到就已經有人按表情：
    // 訊息之後接著發 reaction，不然之後的比對都以這版為準，這個表情永遠不會發出去
    [Fact]
    public async Task Reactions_already_on_a_new_message_are_published_after_it()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var events = new NewEvents(db);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());
        var ct = TestContext.Current.CancellationToken;
        await ingestor.UpsertAsync(Msg("50", version: 50) with { From = "8:alice", FromName = "Alice" }, "{}", publish: false, ct);

        const string pinned = """{"properties":{"emotions":[{"key":"1f4cc_pushpin","users":[{"mri":"8:alice","time":1792726751315}]}]}}""";
        Assert.Equal(IngestResult.Inserted, await ingestor.UpsertAsync(Msg("100", version: 200), pinned, publish: true, ct));

        var published = events.Take();
        Assert.Equal([EventKinds.Message, EventKinds.Reaction], published.Select(e => e.Kind));
        Assert.Equal("100", MessageOf(published[0]).Id);
        Assert.True(published[1].Seq > published[0].Seq); // relay 先貼訊息，表情才有地方掛
        Assert.Equal(
            new ReactionDto("100", "19:x@thread.v2", "1f4cc_pushpin", "8:alice", "Alice", Added: true, DateTimeOffset.FromUnixTimeMilliseconds(1792726751315), Count: 1),
            ReactionOf(published[1]));

        // 同一版又來一次（Trouter 晚到、下一輪輪詢）：不重發
        Assert.Equal(IngestResult.Unchanged, await ingestor.UpsertAsync(Msg("100", version: 200), pinned, publish: true, ct));
        Assert.Empty(events.Take());

        // 只存不發的回補：表情也不發
        await ingestor.UpsertAsync(Msg("101", version: 101), pinned, publish: false, ct);
        Assert.Empty(events.Take());
    }

    // 只按表情、沒在訂閱群組講過話的人：DB 查不到名字，改問 Teams；DB 有的就不問
    [Fact]
    public async Task Unknown_reactor_names_are_looked_up()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var events = new NewEvents(db);
        var people = new FakePeople(new Dictionary<string, string> { ["8:bob"] = "Bob Lin" });
        var ingestor = new MessageIngestor(db, new GatewayEventBus(), people);
        var ct = TestContext.Current.CancellationToken;
        await ingestor.UpsertAsync(Msg("50", version: 50) with { From = "8:alice", FromName = "Alice" }, "{}", publish: false, ct);
        await ingestor.UpsertAsync(Msg("100", version: 100), "{}", publish: false, ct);

        const string reacted = """{"properties":{"emotions":[{"key":"like","users":[{"mri":"8:alice","time":1792640000000},{"mri":"8:bob","time":1792640000000},{"mri":"8:carol","time":1792640000000}]}]}}""";
        await ingestor.UpsertAsync(Msg("100", version: 200), reacted, publish: true, ct);

        var names = events.Take().Select(ReactionOf).ToDictionary(r => r.UserMri, r => r.UserName);
        Assert.Equal(new Dictionary<string, string?> { ["8:alice"] = "Alice", ["8:bob"] = "Bob Lin", ["8:carol"] = null }, names);
        Assert.Equal(["8:bob", "8:carol"], people.Asked.Order());
    }

    // 查人名要打 Teams（可能好幾秒，換 token 更久）：不能握著寫入鎖等，不然 Trouter、輪詢的其他訊息全部卡住
    [Fact]
    public async Task Slow_name_lookup_does_not_block_other_messages()
    {
        using var db = TestDb.OnDisk();
        await AddConversationAsync(db, subscribed: true);
        var people = new BlockingPeople();
        var ingestor = new MessageIngestor(db, new GatewayEventBus(), people);
        var ct = TestContext.Current.CancellationToken;
        await ingestor.UpsertAsync(Msg("100", version: 100), "{}", publish: false, ct);

        const string reacted = """{"properties":{"emotions":[{"key":"like","users":[{"mri":"8:bob","time":1792640000000}]}]}}""";
        var lookingUp = ingestor.UpsertAsync(Msg("100", version: 200), reacted, publish: true, ct);
        await people.Started.WaitAsync(ct); // 正在查 Bob 的名字

        var other = ingestor.UpsertAsync(Msg("101", version: 101), "{}", publish: true, ct);
        var first = await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(5), ct));
        people.Release();
        await lookingUp;
        await other;

        Assert.Same(other, first);
    }

    // 查名字跟寫入之間，同一則訊息被別的來源（例如輪詢）先更新了：照寫入當下的狀態比對，不重發、不漏發
    [Fact]
    public async Task Reactions_are_diffed_against_the_state_at_write_time()
    {
        using var db = TestDb.OnDisk();
        await AddConversationAsync(db, subscribed: true);
        var events = new NewEvents(db);
        var people = new BlockingPeople();
        var ingestor = new MessageIngestor(db, new GatewayEventBus(), people);
        var ct = TestContext.Current.CancellationToken;
        await ingestor.UpsertAsync(Msg("100", version: 100), "{}", publish: false, ct);

        const string bob = """{"properties":{"emotions":[{"key":"like","users":[{"mri":"8:bob","time":1792640000000}]}]}}""";
        var trouter = ingestor.UpsertAsync(Msg("100", version: 200), bob, publish: true, ct);
        await people.Started.WaitAsync(ct);
        // 查名字的同時，輪詢拿到同一版先寫進去了
        var poll = ingestor.UpsertAsync(Msg("100", version: 200), bob, publish: true, ct);
        people.Release();
        await Task.WhenAll(trouter, poll);

        var reaction = Assert.Single(events.Take());
        Assert.Equal(("8:bob", "Bob"), (ReactionOf(reaction).UserMri, ReactionOf(reaction).UserName));
    }

    /// <summary>查名字時停住，等測試 <see cref="Release"/> 才回（每個人都叫 Bob）。</summary>
    private sealed class BlockingPeople : IPeopleDirectory
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyDictionary<string, string>> DisplayNamesAsync(IReadOnlyCollection<string> mris, CancellationToken ct)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return mris.ToDictionary(m => m, _ => "Bob");
        }
    }

    private sealed class FakePeople(IReadOnlyDictionary<string, string> known) : IPeopleDirectory
    {
        public List<string> Asked { get; } = [];

        public Task<IReadOnlyDictionary<string, string>> DisplayNamesAsync(IReadOnlyCollection<string> mris, CancellationToken ct)
        {
            Asked.AddRange(mris);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                mris.Where(known.ContainsKey).ToDictionary(m => m, m => known[m]));
        }
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

    // 事件內容只進 log；commit 之後才通知 bus，帶的是當下最新的序號（ADR 0006）
    [Fact]
    public async Task Publish_records_event_then_notifies_its_sequence()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var bus = new GatewayEventBus();
        using var sub = bus.Subscribe();
        var ingestor = new MessageIngestor(db, bus);
        var ct = TestContext.Current.CancellationToken;

        await ingestor.UpsertAsync(Msg("100", version: 100), "{}", publish: true, ct);
        Assert.True(sub.Reader.TryRead(out var inserted));
        await ingestor.UpsertAsync(Msg("100", version: 200, content: "改過", edited: true), "{}", publish: true, ct);
        Assert.True(sub.Reader.TryRead(out var edited));

        Assert.True(edited > inserted); // 編輯同訊息 id，但是新的事件序號
        await using var ctx = db.CreateDbContext();
        Assert.Equal([inserted, edited], ctx.PublishedEvents.OrderBy(e => e.Seq).Select(e => e.Seq));
    }

    // 事件契約：payload 是 DTO 的欄位 + schemaVersion + raw（Teams 原始 JSON），照 DTO 解析的 consumer 不受影響
    [Fact]
    public async Task Event_payload_carries_schema_version_and_raw_json()
    {
        using var db = new TestDb();
        await AddConversationAsync(db, subscribed: true);
        var events = new NewEvents(db);
        var ingestor = new MessageIngestor(db, new GatewayEventBus());

        await ingestor.UpsertAsync(Msg("100", version: 100), """{"id":"100","properties":{"importance":"high"}}""", publish: true, TestContext.Current.CancellationToken);

        var row = Assert.Single(events.Take());
        Assert.Equal(EventSchema.Version, row.SchemaVersion);
        using var payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(EventSchema.Version, payload.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("high", payload.RootElement.GetProperty("raw").GetProperty("properties").GetProperty("importance").GetString());
        Assert.Equal("100", MessageOf(row).Id);
    }

    /// <summary>從內部 log 讀上次之後新寫進去的事件（事件內容不經過 bus）。</summary>
    private sealed class NewEvents(TestDb db)
    {
        private long _seen;

        public List<PublishedEvent> Take()
        {
            using var ctx = db.CreateDbContext();
            var rows = ctx.PublishedEvents.Where(e => e.Seq > _seen).OrderBy(e => e.Seq).ToList();
            if (rows.Count > 0)
                _seen = rows[^1].Seq;
            return rows;
        }
    }

    private static MessageDto MessageOf(PublishedEvent e) => JsonSerializer.Deserialize(e.Payload, ContractsJsonContext.Default.MessageDto)!;

    private static ReactionDto ReactionOf(PublishedEvent e) => JsonSerializer.Deserialize(e.Payload, ContractsJsonContext.Default.ReactionDto)!;

    [Fact]
    public async Task Concurrent_upserts_of_the_same_message_insert_once()
    {
        // Trouter 推來的同時輪詢也抓到同一則：先查再插不能撞主鍵
        using var db = TestDb.OnDisk();
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
