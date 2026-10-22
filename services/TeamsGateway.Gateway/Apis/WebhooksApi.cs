using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsGateway.Gateway.Auth;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;

namespace TeamsGateway.Gateway.Apis;

/// <summary><c>/v1/webhooks</c>：訂閱者登記 webhook，有符合的新訊息時 gateway 會 POST 過去（帶 HMAC 簽章）。帶 API key。</summary>
public static class WebhooksApi
{
    public static void MapWebhooksApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/webhooks").AddEndpointFilter<ApiKeyFilter>().WithTags("Webhooks");

        group.MapPost("", async (CreateWebhookRequest body, HttpContext http, IDbContextFactory<GatewayDbContext> factory, IOptions<GatewayOptions> options, CancellationToken ct) =>
        {
            if (!Uri.TryCreate(body.TargetUrl, UriKind.Absolute, out var target) || !WebhookTargetGuard.IsAllowedScheme(target))
                return Results.BadRequest(new { error = "targetUrl 要是合法的 http(s) 絕對網址" });
            // IP 直接寫在網址裡的先擋，早點回饋；主機名在投遞連線時才檢查（見 WebhookTargetGuard）
            if (IPAddress.TryParse(target.IdnHost, out var ip) && !WebhookTargetGuard.IsAllowedAddress(ip, options.Value.WebhookAllowPrivateNetworks))
                return Results.BadRequest(new { error = "targetUrl 不能指向內網 / loopback / link-local 位址" });

            var apiKey = (ApiKey)http.Items[nameof(ApiKey)]!;
            var hook = new WebhookSubscription
            {
                Id = Guid.NewGuid(),
                ApiKeyId = apiKey.Id,
                ConversationId = string.IsNullOrWhiteSpace(body.ConversationId) ? null : body.ConversationId,
                TargetUrl = body.TargetUrl,
                Secret = body.Secret,
                Active = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await using var db = await factory.CreateDbContextAsync(ct);
            db.WebhookSubscriptions.Add(hook);
            await db.SaveChangesAsync(ct);
            return Results.Ok(View(hook));
        })
        .WithSummary("登記 webhook")
        .WithDescription("有符合的新訊息時 gateway 會 POST 到 `targetUrl`，帶 `X-TeamsGateway-Signature: sha256=<hmac>`。`conversationId` 省略 = 所有訂閱對話。");

        group.MapGet("", async (HttpContext http, IDbContextFactory<GatewayDbContext> factory, CancellationToken ct) =>
        {
            var apiKey = (ApiKey)http.Items[nameof(ApiKey)]!;
            await using var db = await factory.CreateDbContextAsync(ct);
            var hooks = await db.WebhookSubscriptions.Where(w => w.ApiKeyId == apiKey.Id).ToListAsync(ct);
            return Results.Ok(hooks.Select(View));
        })
        .WithSummary("列出自己登記的 webhook");

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, IDbContextFactory<GatewayDbContext> factory, CancellationToken ct) =>
        {
            var apiKey = (ApiKey)http.Items[nameof(ApiKey)]!;
            await using var db = await factory.CreateDbContextAsync(ct);
            var hook = await db.WebhookSubscriptions.FirstOrDefaultAsync(w => w.Id == id && w.ApiKeyId == apiKey.Id, ct);
            if (hook is null)
                return Results.NotFound();
            db.WebhookSubscriptions.Remove(hook);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithSummary("刪除 webhook");
    }

    private static object View(WebhookSubscription w) =>
        new { w.Id, w.ConversationId, w.TargetUrl, hasSecret = !string.IsNullOrEmpty(w.Secret), w.Active, w.CreatedAt };
}

public sealed record CreateWebhookRequest(string TargetUrl, string? ConversationId = null, string? Secret = null);
