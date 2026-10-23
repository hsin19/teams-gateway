using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Auth;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

/// <summary>webhook sink（ADR 0006）：各自的游標、依序送、成功才推進、失敗重送同一筆、落後太久停用。</summary>
public class WebhookSinkTests
{
    private const string Conv = "19:x@thread.v2";
    private readonly WebhookSecretProtector _secrets = new(new EphemeralDataProtectionProvider());

    [Fact]
    public async Task Revoked_key_and_inactive_webhooks_are_not_delivered()
    {
        using var db = new TestDb();
        var ct = TestContext.Current.CancellationToken;
        var keys = new ApiKeyService(db);
        var (kept, _) = await keys.CreateAsync("kept", ct);
        var (revoked, _) = await keys.CreateAsync("revoked", ct);
        await using (var ctx = db.CreateDbContext())
        {
            ctx.WebhookSubscriptions.AddRange(
                Hook(kept.Id, "https://kept.example/all"),
                Hook(kept.Id, "https://kept.example/inactive", active: false),
                Hook(revoked.Id, "https://revoked.example/all"));
            await ctx.SaveChangesAsync(ct);
        }
        await keys.RevokeAsync(revoked.Id, ct);

        var hooks = await WebhookSink.ActiveHooksAsync(db, ct);

        Assert.Equal(["https://kept.example/all"], hooks.Select(h => h.TargetUrl));
    }

    // 依序送、header 帶事件序號與格式版本、body 簽 HMAC；送成功才推進游標
    [Fact]
    public async Task Delivers_pending_events_in_order_and_advances_the_cursor()
    {
        using var db = new TestDb();
        var ct = TestContext.Current.CancellationToken;
        var hook = await AddHookAsync(db, secret: "s3cret");
        await PublishAsync(db, "100", "200");
        var target = new RecordingTarget();

        Assert.True(await Sink(db, target).DrainAsync(hook, ct));

        Assert.Equal(["1", "2"], target.Requests.Select(r => r.Seq));
        var first = target.Requests[0];
        Assert.Equal((EventKinds.Message, "1"), (first.Kind, first.SchemaVersion));
        Assert.Equal("sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData("s3cret"u8.ToArray(), Encoding.UTF8.GetBytes(first.Body))), first.Signature);
        Assert.Equal(2, await CursorAsync(db, hook.Id));
    }

    // 目標掛了：停在那筆、不跳過；下次從同一筆重送
    [Fact]
    public async Task Failed_delivery_keeps_the_cursor_and_retries_the_same_event()
    {
        using var db = new TestDb();
        var ct = TestContext.Current.CancellationToken;
        var hook = await AddHookAsync(db);
        await PublishAsync(db, "100", "200");
        var target = new RecordingTarget { FailFirst = 2 }; // 第 1 筆成功前先失敗兩次

        Assert.False(await Sink(db, target).DrainAsync(hook, ct));
        Assert.Equal(0, await CursorAsync(db, hook.Id));

        Assert.False(await Sink(db, target).DrainAsync(await ReloadAsync(db, hook.Id), ct));
        Assert.True(await Sink(db, target).DrainAsync(await ReloadAsync(db, hook.Id), ct));

        Assert.Equal(["1", "1", "1", "2"], target.Requests.Select(r => r.Seq));
        Assert.Equal(2, await CursorAsync(db, hook.Id));
    }

    // 只訂某個對話：別的對話的事件不送，但游標照樣往前（不然 pruner 永遠清不掉）
    [Fact]
    public async Task Other_conversations_are_skipped_but_the_cursor_moves_on()
    {
        using var db = new TestDb();
        var ct = TestContext.Current.CancellationToken;
        var hook = await AddHookAsync(db, conversationId: "19:other@thread.v2");
        await PublishAsync(db, "100");
        var target = new RecordingTarget();

        Assert.True(await Sink(db, target).DrainAsync(hook, ct));

        Assert.Empty(target.Requests);
        Assert.Equal(1, await CursorAsync(db, hook.Id));
    }

    // 落後超過保留天數還送不出去：停用，不讓它撐著事件 log 無限長大
    [Fact]
    public async Task Webhook_lagging_beyond_retention_is_deactivated()
    {
        using var db = new TestDb();
        var ct = TestContext.Current.CancellationToken;
        var hook = await AddHookAsync(db);
        await using (var ctx = db.CreateDbContext())
        {
            ctx.PublishedEvents.Add(new PublishedEvent
            {
                MessageId = "100", ConversationId = Conv, Payload = "{}", SchemaVersion = 1,
                PublishedAt = DateTimeOffset.UtcNow.AddDays(-8),
            });
            await ctx.SaveChangesAsync(ct);
        }

        Assert.False(await Sink(db, new RecordingTarget { FailFirst = 1 }).DrainAsync(hook, ct));

        Assert.False((await ReloadAsync(db, hook.Id)).Active);
    }

    // pruner 只清所有 sink 都送過、又超過保留天數的事件；沒 webhook 等著就只看時間
    [Fact]
    public async Task Pruner_keeps_old_events_a_webhook_has_not_received()
    {
        using var db = new TestDb();
        var ct = TestContext.Current.CancellationToken;
        await PublishAsync(db, "100", "200", "300");
        await using (var ctx = db.CreateDbContext())
            await ctx.PublishedEvents.ExecuteUpdateAsync(s => s.SetProperty(e => e.PublishedAt, DateTimeOffset.UtcNow.AddDays(-8)), ct);
        var hook = await AddHookAsync(db);
        await using (var ctx = db.CreateDbContext())
            await ctx.WebhookSubscriptions.ExecuteUpdateAsync(s => s.SetProperty(w => w.DeliveredSeq, 1L), ct); // 只送到 1
        var sink = Sink(db, new RecordingTarget());
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);

        Assert.Equal(1, await PublishedEventPruner.PruneAsync(db, [sink], cutoff, ct));
        await using (var ctx = db.CreateDbContext())
            Assert.Equal([2L, 3L], ctx.PublishedEvents.OrderBy(e => e.Seq).Select(e => e.Seq));

        await using (var ctx = db.CreateDbContext())
            await ctx.WebhookSubscriptions.Where(w => w.Id == hook.Id).ExecuteDeleteAsync(ct);
        Assert.Equal(2, await PublishedEventPruner.PruneAsync(db, [sink], cutoff, ct));
    }

    private WebhookSink Sink(TestDb db, RecordingTarget target) =>
        new(new GatewayEventBus(), db, new SingleClientFactory(target), _secrets,
            Options.Create(new GatewayOptions { EventRetentionDays = 7 }), NullLogger<WebhookSink>.Instance);

    private async Task<WebhookSubscription> AddHookAsync(TestDb db, string? conversationId = null, string? secret = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var (key, _) = await new ApiKeyService(db).CreateAsync("hook", ct);
        var hook = Hook(key.Id, "https://hook.example/in", conversationId);
        hook.Secret = _secrets.Protect(secret);
        await using var ctx = db.CreateDbContext();
        ctx.WebhookSubscriptions.Add(hook);
        await ctx.SaveChangesAsync(ct);
        return hook;
    }

    private static async Task PublishAsync(TestDb db, params string[] messageIds)
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var ctx = db.CreateDbContext())
        {
            if (!await ctx.Conversations.AnyAsync(ct))
            {
                ctx.Conversations.Add(new Conversation { Id = Conv, Subscribed = true, UpdatedAt = DateTimeOffset.UtcNow });
                await ctx.SaveChangesAsync(ct);
            }
        }
        var ingestor = new MessageIngestor(db, new GatewayEventBus());
        foreach (var id in messageIds)
            await ingestor.UpsertAsync(new MessageDto(id, Conv, "8:me", "me", "Text", "hi", 1, DateTimeOffset.UtcNow, long.Parse(id)), "{}", publish: true, ct);
    }

    private static async Task<long> CursorAsync(TestDb db, Guid hookId) => (await ReloadAsync(db, hookId)).DeliveredSeq;

    private static async Task<WebhookSubscription> ReloadAsync(TestDb db, Guid hookId)
    {
        await using var ctx = db.CreateDbContext();
        return await ctx.WebhookSubscriptions.AsNoTracking().SingleAsync(w => w.Id == hookId, TestContext.Current.CancellationToken);
    }

    private static WebhookSubscription Hook(Guid apiKeyId, string url, string? conversationId = null, bool active = true) => new()
    {
        Id = Guid.NewGuid(),
        ApiKeyId = apiKeyId,
        ConversationId = conversationId,
        TargetUrl = url,
        Active = active,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>假的 webhook 目標：記下每次收到的請求；<see cref="FailFirst"/> 次之前都回 503。</summary>
    private sealed class RecordingTarget : HttpMessageHandler
    {
        public int FailFirst { get; init; }

        public List<(string Seq, string Kind, string SchemaVersion, string? Signature, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string Header(string name) => request.Headers.TryGetValues(name, out var v) ? v.Single() : "";
            Requests.Add((Header("X-TeamsGateway-Seq"), Header("X-TeamsGateway-Event"), Header("X-TeamsGateway-Schema-Version"),
                request.Headers.TryGetValues("X-TeamsGateway-Signature", out var sig) ? sig.Single() : null,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Requests.Count <= FailFirst ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
