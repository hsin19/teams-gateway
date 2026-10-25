using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Events;

/// <summary>
/// webhook sink（ADR 0006）：每個 webhook 有自己的游標（<see cref="WebhookSubscription.DeliveredSeq"/>），
/// 從游標往後讀內部 log、依序 POST，成功才推進；失敗就退避後重送同一筆、不跳過（at-least-once，
/// 接收端照 <c>X-TeamsGateway-Seq</c> 去重）。一個目標掛了只卡它自己。
/// 落後超過 <c>EventRetentionDays</c> 就停用：不讓一個死掉的目標撐著事件 log 無限長大。
/// </summary>
public sealed class WebhookSink(
    GatewayEventBus bus,
    IDbContextFactory<GatewayDbContext> factory,
    IHttpClientFactory httpFactory,
    WebhookSecretProtector secrets,
    IOptions<GatewayOptions> options,
    ILogger<WebhookSink> logger) : BackgroundService, IEventSink
{
    public const string SinkName = "Webhook";

    /// <summary>投遞用的 named HttpClient（連線時檢查目標 IP，見 <see cref="WebhookTargetGuard"/>）。</summary>
    public const string HttpClientName = "webhook";

    private const int Batch = 100;
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    // 沒有新事件也定期醒來：到期的重試、剛登記的 webhook
    private static readonly TimeSpan IdleWake = TimeSpan.FromMinutes(1);

    // webhook id → 下次重試的時間與目前的退避；只在投遞迴圈裡讀寫
    private readonly Dictionary<Guid, (DateTimeOffset At, TimeSpan Backoff)> _retry = [];

    // webhook id → 正在送的那一趟（送到最新、或送失敗為止）；只在投遞迴圈裡讀寫
    private readonly Dictionary<Guid, Task<bool>> _inFlight = [];

    public string Name => SinkName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = bus.Subscribe();
        Task<bool>? notified = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Webhook dispatch failed");
            }

            // 有新事件、有一趟送完（它可能還有新的要送，或要排重試）、或有重試到期，就再派一次
            notified ??= subscription.WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var wake = Task.Delay(NextWake(), timer.Token);
            var woke = await Task.WhenAny([notified, wake, .. _inFlight.Values]);
            await timer.CancelAsync();
            if (woke == notified)
                notified = null;
        }

        // 關機：stoppingToken 已經取消了正在送的那幾趟，等它們收掉，不留在背景碰 DB
        await Task.WhenAll(_inFlight.Values.Cast<Task>()).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public async Task<long?> OldestPendingAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cursor = await db.WebhookSubscriptions
            .Where(w => w.Active && w.ApiKey!.RevokedAt == null)
            .MinAsync(w => (long?)w.DeliveredSeq, ct);
        return cursor + 1;
    }

    /// <summary>
    /// 收掉送完的那幾趟（送失敗的排重試），再讓沒在送、也沒在退避中的 webhook 各自開一趟送到最新。
    /// 不等任何一趟送完：一個目標很慢（還沒失敗、也還沒逾時）只卡它自己，其他 webhook 的新事件照樣即時送。
    /// </summary>
    internal async Task DispatchAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, trip) in _inFlight.Where(t => t.Value.IsCompleted).ToList())
        {
            _inFlight.Remove(id);
            if (trip.IsFaulted)
                logger.LogError(trip.Exception, "Webhook {Id} delivery failed", id);
            if (trip.IsCompletedSuccessfully && trip.Result)
            {
                _retry.Remove(id);
                continue;
            }
            var backoff = _retry.TryGetValue(id, out var last)
                ? TimeSpan.FromTicks(Math.Min(last.Backoff.Ticks * 2, MaxBackoff.Ticks))
                : MinBackoff;
            _retry[id] = (now + backoff, backoff);
        }

        var hooks = await ActiveHooksAsync(factory, ct);
        foreach (var gone in _retry.Keys.Except(hooks.Select(h => h.Id)).ToList())
            _retry.Remove(gone); // 刪掉 / 停用的不用再排重試

        // 游標只有自己那一趟會推進，所以等它送完才重讀、開下一趟
        foreach (var hook in hooks)
            if (!_inFlight.ContainsKey(hook.Id) && (!_retry.TryGetValue(hook.Id, out var r) || r.At <= now))
                _inFlight[hook.Id] = DrainAsync(hook, ct);
    }

    /// <summary>
    /// 把一個 webhook 從它的游標送到最新：成功一筆記一筆（重啟後不重送），對話不符的直接略過。
    /// 送失敗就停在那筆、回 false，下次從同一筆重送。
    /// </summary>
    internal async Task<bool> DrainAsync(WebhookSubscription hook, CancellationToken ct)
    {
        var cursor = hook.DeliveredSeq;
        var saved = cursor;
        while (true)
        {
            var batch = await EventLog.ReadAfterAsync(factory, cursor, Batch, ct);
            foreach (var evt in batch)
            {
                if (hook.ConversationId is null || hook.ConversationId == evt.ConversationId)
                {
                    if (await PostAsync(hook, evt, ct) is { } error)
                    {
                        if (cursor != saved)
                            await SaveCursorAsync(hook.Id, cursor, ct);
                        await FailedAsync(hook, evt, error, ct);
                        return false;
                    }
                    await SaveCursorAsync(hook.Id, evt.Seq, ct);
                    saved = evt.Seq;
                }
                cursor = evt.Seq;
            }
            if (batch.Count < Batch)
            {
                if (cursor != saved)
                    await SaveCursorAsync(hook.Id, cursor, ct);
                return true;
            }
        }
    }

    /// <summary>要投遞的 webhook：啟用中，而且登記它的 API key 沒被撤銷。</summary>
    internal static async Task<List<WebhookSubscription>> ActiveHooksAsync(IDbContextFactory<GatewayDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WebhookSubscriptions.Where(w => w.Active && w.ApiKey!.RevokedAt == null).AsNoTracking().ToListAsync(ct);
    }

    /// <summary>POST 一個事件；成功回 null，失敗回原因（給 log）。</summary>
    private async Task<string?> PostAsync(WebhookSubscription hook, PublishedEvent evt, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(evt.Payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, hook.TargetUrl)
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } },
        };
        request.Headers.TryAddWithoutValidation("X-TeamsGateway-Event", evt.Kind); // message / reaction
        request.Headers.TryAddWithoutValidation("X-TeamsGateway-Seq", evt.Seq.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-TeamsGateway-Schema-Version", evt.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        if (secrets.Unprotect(hook.Secret) is { Length: > 0 } secret)
            request.Headers.TryAddWithoutValidation("X-TeamsGateway-Signature", "sha256=" + Sign(secret, body));

        try
        {
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            return response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return ex.Message; // 連不上、逾時（HttpClient.Timeout 也是 OperationCanceledException）
        }
    }

    /// <summary>送不出去：落後超過保留天數就停用，否則記一筆等重試。</summary>
    private async Task FailedAsync(WebhookSubscription hook, PublishedEvent evt, string error, CancellationToken ct)
    {
        var retention = TimeSpan.FromDays(options.Value.EventRetentionDays);
        if (DateTimeOffset.UtcNow - evt.PublishedAt <= retention)
        {
            logger.LogWarning("webhook {Id} 送事件 {Seq} 失敗（{Error}），稍後重試", hook.Id, evt.Seq, error);
            return;
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.WebhookSubscriptions.Where(w => w.Id == hook.Id).ExecuteUpdateAsync(s => s.SetProperty(w => w.Active, false), ct);
        logger.LogWarning("webhook {Id}（{Url}）落後超過 {Days} 天還送不出去（{Error}），已停用；要恢復就重新登記",
            hook.Id, hook.TargetUrl, options.Value.EventRetentionDays, error);
    }

    private async Task SaveCursorAsync(Guid hookId, long seq, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.WebhookSubscriptions.Where(w => w.Id == hookId).ExecuteUpdateAsync(s => s.SetProperty(w => w.DeliveredSeq, seq), ct);
    }

    private TimeSpan NextWake()
    {
        if (_retry.Count == 0)
            return IdleWake;
        var wait = _retry.Values.Min(r => r.At) - DateTimeOffset.UtcNow;
        return wait <= TimeSpan.Zero ? TimeSpan.Zero : wait < IdleWake ? wait : IdleWake;
    }

    private static string Sign(string secret, byte[] body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
}
