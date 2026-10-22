using TeamsGateway.Gateway.Auth;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;

namespace TeamsGateway.Gateway.UnitTests;

public class WebhookDeliveryTests
{
    private const string Conv = "19:x@thread.v2";

    [Fact]
    public async Task Revoked_key_webhooks_stop_receiving()
    {
        using var db = new TestDb();
        var ct = TestContext.Current.CancellationToken;
        var keys = new ApiKeyService(db);
        var (kept, _) = await keys.CreateAsync("kept", ct);
        var (revoked, _) = await keys.CreateAsync("revoked", ct);
        await using (var ctx = db.CreateDbContext())
        {
            ctx.WebhookSubscriptions.AddRange(
                Hook(kept.Id, "https://kept.example/all", conversationId: null),
                Hook(kept.Id, "https://kept.example/this", Conv),
                Hook(kept.Id, "https://kept.example/other", "19:other@thread.v2"),
                Hook(kept.Id, "https://kept.example/inactive", Conv, active: false),
                Hook(revoked.Id, "https://revoked.example/all", conversationId: null));
            await ctx.SaveChangesAsync(ct);
        }
        await keys.RevokeAsync(revoked.Id, ct);

        var hooks = await WebhookDeliveryService.HooksForAsync(db, Conv, ct);

        Assert.Equal(["https://kept.example/all", "https://kept.example/this"], hooks.Select(h => h.TargetUrl).Order());
    }

    private static WebhookSubscription Hook(Guid apiKeyId, string url, string? conversationId, bool active = true) => new()
    {
        Id = Guid.NewGuid(),
        ApiKeyId = apiKeyId,
        ConversationId = conversationId,
        TargetUrl = url,
        Active = active,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
