using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Events;

/// <summary>消費事件匯流排，把新訊息 POST 給符合的 webhook 訂閱（帶 HMAC 簽章）。投遞失敗記 log 跳過。</summary>
public sealed class WebhookDeliveryService(
    GatewayEventBus bus,
    IDbContextFactory<GatewayDbContext> factory,
    IHttpClientFactory httpFactory,
    WebhookSecretProtector secrets,
    ILogger<WebhookDeliveryService> logger) : BackgroundService
{
    /// <summary>投遞用的 named HttpClient（連線時檢查目標 IP，見 <see cref="WebhookTargetGuard"/>）。</summary>
    public const string HttpClientName = "webhook";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = bus.Subscribe();
        await foreach (var evt in subscription.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await DeliverAsync(evt.Message, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "webhook 投遞失敗");
            }
        }
    }

    private async Task DeliverAsync(MessageDto message, CancellationToken ct)
    {
        var hooks = await HooksForAsync(factory, message.ConversationId, ct);
        if (hooks.Count == 0)
            return;

        var body = JsonSerializer.Serialize(message, ContractsJsonContext.Default.MessageDto);
        var bytes = Encoding.UTF8.GetBytes(body);
        var client = httpFactory.CreateClient(HttpClientName);

        foreach (var hook in hooks)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, hook.TargetUrl)
                {
                    Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/json") } },
                };
                request.Headers.TryAddWithoutValidation("X-TeamsGateway-Event", "message");
                if (secrets.Unprotect(hook.Secret) is { Length: > 0 } secret)
                    request.Headers.TryAddWithoutValidation("X-TeamsGateway-Signature", "sha256=" + Sign(secret, bytes));

                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    logger.LogWarning("webhook {Id} 回 HTTP {Status}", hook.Id, (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "webhook {Id} 投遞失敗（{Url}）", hook.Id, hook.TargetUrl);
            }
        }
    }

    /// <summary>要收這個對話事件的 webhook：啟用中、訂了這個對話（或全部），而且登記它的 API key 沒被撤銷。</summary>
    internal static async Task<List<WebhookSubscription>> HooksForAsync(
        IDbContextFactory<GatewayDbContext> factory, string conversationId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WebhookSubscriptions
            .Where(w => w.Active && w.ApiKey!.RevokedAt == null)
            .Where(w => w.ConversationId == null || w.ConversationId == conversationId)
            .ToListAsync(ct);
    }

    private static string Sign(string secret, byte[] body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
}
