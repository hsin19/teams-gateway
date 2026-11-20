using System.Net;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Auth;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Events;

namespace TeamsGateway.Gateway.Apis;

/// <summary><c>/v1/webhooks</c>：訂閱者登記 webhook，有符合的新事件時 gateway 會 POST 過去（帶 HMAC 簽章，送失敗會重送，見 <see cref="WebhookSink"/>）。帶 API key。</summary>
public static class WebhooksApi
{
    public static void MapWebhooksApi(this IEndpointRouteBuilder app)
    {
        // webhook 掛在建立它的 API key 底下（撤銷 key 就一起失效），所以要用 API key 打
        var group = app.MapGroup("/v1/webhooks").RequireAuthorization(GatewayRoles.Api).WithTags("Webhooks");

        group.MapPost("", async Task<Results<Ok<WebhookDto>, BadRequest<ApiError>>> (
            CreateWebhookRequest body, CurrentUser user, IDbContextFactory<GatewayDbContext> factory, IOptions<GatewayOptions> options,
            WebhookSecretProtector secrets, CancellationToken ct) =>
        {
            if (!Uri.TryCreate(body.TargetUrl, UriKind.Absolute, out var target) || !WebhookTargetGuard.IsAllowedScheme(target))
                return TypedResults.BadRequest(new ApiError("targetUrl must be an absolute http(s) URL"));
            // IP 直接寫在網址裡的先擋，早點回饋；主機名在投遞連線時才檢查（見 WebhookTargetGuard）
            if (IPAddress.TryParse(target.IdnHost, out var ip) && !WebhookTargetGuard.IsAllowedAddress(ip, options.Value.WebhookAllowPrivateNetworks))
                return TypedResults.BadRequest(new ApiError("targetUrl must not point at a private, loopback or link-local address"));

            if (user.ApiKeyId is not { } apiKeyId)
                return TypedResults.BadRequest(new ApiError("Webhooks belong to an API key: call this with an API key"));
            await using var db = await factory.CreateDbContextAsync(ct);
            var hook = new WebhookSubscription
            {
                Id = Guid.NewGuid(),
                ApiKeyId = apiKeyId,
                ConversationId = string.IsNullOrWhiteSpace(body.ConversationId) ? null : body.ConversationId,
                TargetUrl = body.TargetUrl,
                Secret = secrets.Protect(body.Secret), // 加密後才存
                Active = true,
                CreatedAt = DateTimeOffset.UtcNow,
                DeliveredSeq = await EventLog.MaxSeqAsync(factory, ct), // 只收登記之後的事件
            };
            db.WebhookSubscriptions.Add(hook);
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(View(hook));
        })
        .WithSummary("Register a webhook")
        .WithDescription(
            "The gateway POSTs each matching new event to `targetUrl` (the body is the SSE `data`) with the headers `X-TeamsGateway-Event`, "
            + "`X-TeamsGateway-Seq` (the event sequence number; failed deliveries are retried, so dedupe on it) and `X-TeamsGateway-Schema-Version`, "
            + "plus `X-TeamsGateway-Signature: sha256=<hmac>` when a secret is set. Only events after registration are delivered; "
            + "omit `conversationId` for all subscribed chats.");

        group.MapGet("", async (CurrentUser user, IDbContextFactory<GatewayDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var hooks = await db.WebhookSubscriptions.Where(w => w.ApiKeyId == user.ApiKeyId).ToListAsync(ct);
            return TypedResults.Ok<IReadOnlyList<WebhookDto>>(hooks.Select(View).ToList());
        })
        .WithSummary("List the webhooks this API key registered");

        group.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound>> (Guid id, CurrentUser user, IDbContextFactory<GatewayDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var hook = await db.WebhookSubscriptions.FirstOrDefaultAsync(w => w.Id == id && w.ApiKeyId == user.ApiKeyId, ct);
            if (hook is null)
                return TypedResults.NotFound();
            db.WebhookSubscriptions.Remove(hook);
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        })
        .WithSummary("Delete a webhook");
    }

    private static WebhookDto View(WebhookSubscription w) =>
        new(w.Id, w.ConversationId, w.TargetUrl, HasSecret: !string.IsNullOrEmpty(w.Secret), w.Active, w.CreatedAt);
}
