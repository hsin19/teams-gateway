using System.Net;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;
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

        group.MapPost("", async Task<Results<Ok<WebhookDto>, BadRequest<ApiError>>> (
            CreateWebhookRequest body, HttpContext http, IDbContextFactory<GatewayDbContext> factory, IOptions<GatewayOptions> options,
            WebhookSecretProtector secrets, CancellationToken ct) =>
        {
            if (!Uri.TryCreate(body.TargetUrl, UriKind.Absolute, out var target) || !WebhookTargetGuard.IsAllowedScheme(target))
                return TypedResults.BadRequest(new ApiError("targetUrl 要是合法的 http(s) 絕對網址"));
            // IP 直接寫在網址裡的先擋，早點回饋；主機名在投遞連線時才檢查（見 WebhookTargetGuard）
            if (IPAddress.TryParse(target.IdnHost, out var ip) && !WebhookTargetGuard.IsAllowedAddress(ip, options.Value.WebhookAllowPrivateNetworks))
                return TypedResults.BadRequest(new ApiError("targetUrl 不能指向內網 / loopback / link-local 位址"));

            var apiKey = (ApiKey)http.Items[nameof(ApiKey)]!;
            var hook = new WebhookSubscription
            {
                Id = Guid.NewGuid(),
                ApiKeyId = apiKey.Id,
                ConversationId = string.IsNullOrWhiteSpace(body.ConversationId) ? null : body.ConversationId,
                TargetUrl = body.TargetUrl,
                Secret = secrets.Protect(body.Secret), // 加密後才存
                Active = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await using var db = await factory.CreateDbContextAsync(ct);
            db.WebhookSubscriptions.Add(hook);
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(View(hook));
        })
        .WithSummary("登記 webhook")
        .WithDescription("有符合的新訊息時 gateway 會 POST 到 `targetUrl`，帶 `X-TeamsGateway-Signature: sha256=<hmac>`。`conversationId` 省略 = 所有訂閱對話。");

        group.MapGet("", async (HttpContext http, IDbContextFactory<GatewayDbContext> factory, CancellationToken ct) =>
        {
            var apiKey = (ApiKey)http.Items[nameof(ApiKey)]!;
            await using var db = await factory.CreateDbContextAsync(ct);
            var hooks = await db.WebhookSubscriptions.Where(w => w.ApiKeyId == apiKey.Id).ToListAsync(ct);
            return TypedResults.Ok<IReadOnlyList<WebhookDto>>(hooks.Select(View).ToList());
        })
        .WithSummary("列出自己登記的 webhook");

        group.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound>> (Guid id, HttpContext http, IDbContextFactory<GatewayDbContext> factory, CancellationToken ct) =>
        {
            var apiKey = (ApiKey)http.Items[nameof(ApiKey)]!;
            await using var db = await factory.CreateDbContextAsync(ct);
            var hook = await db.WebhookSubscriptions.FirstOrDefaultAsync(w => w.Id == id && w.ApiKeyId == apiKey.Id, ct);
            if (hook is null)
                return TypedResults.NotFound();
            db.WebhookSubscriptions.Remove(hook);
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        })
        .WithSummary("刪除 webhook");
    }

    private static WebhookDto View(WebhookSubscription w) =>
        new(w.Id, w.ConversationId, w.TargetUrl, HasSecret: !string.IsNullOrEmpty(w.Secret), w.Active, w.CreatedAt);
}
