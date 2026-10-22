namespace TeamsGateway.Gateway.Teams;

/// <summary>常駐跑 Trouter 連線：收到「訂閱中對話」的新訊息就去重寫進 DB、publish 給 SSE / webhook。斷線重連，退避。</summary>
public sealed class TrouterBackgroundService(
    TrouterClient trouter,
    MessageIngestor ingestor,
    GatewayMetrics metrics,
    ILogger<TrouterBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.Zero;
        while (!stoppingToken.IsCancellationRequested)
        {
            var connectedAt = DateTimeOffset.UtcNow;
            Exception? error = null;
            try
            {
                await trouter.RunAsync(OnMessageAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                error = ex; // 常見是還沒匯入 cookie（換不到 token）
            }

            // 對方正常關閉也要等：一連上就被關（例如 token 被拒）時不等就會無延遲狂連
            delay = DelayAfter(delay, DateTimeOffset.UtcNow - connectedAt);
            if (error is null)
                logger.LogInformation("Trouter 連線結束，{Delay} 後重連", delay);
            else
                logger.LogWarning(error, "Trouter 連線中斷，{Delay} 後重連", delay);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>這次斷線後等多久：連線撐過 <see cref="StableAfter"/> 才從頭退避，否則在上一次的基礎上加倍。</summary>
    internal static TimeSpan DelayAfter(TimeSpan previous, TimeSpan uptime) =>
        previous == TimeSpan.Zero || uptime >= StableAfter
            ? MinBackoff
            : TimeSpan.FromTicks(Math.Min(previous.Ticks * 2, MaxBackoff.Ticks));

    private async Task OnMessageAsync(IncomingMessage incoming, CancellationToken ct)
    {
        // 只收訂閱中的對話（Trouter 會推帳號所有對話）；其餘丟掉，不入庫
        if (!await ingestor.IsSubscribedAsync(incoming.Message.ConversationId, ct))
            return;
        var result = await ingestor.UpsertAsync(incoming.Message, incoming.RawJson, publish: true, ct);
        metrics.Delivered(IngestSource.Trouter, result, incoming.Message);
    }
}
