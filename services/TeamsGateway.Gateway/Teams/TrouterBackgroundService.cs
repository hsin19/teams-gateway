namespace TeamsGateway.Gateway.Teams;

/// <summary>常駐跑 Trouter 連線：收到「訂閱中對話」的新訊息就去重寫進 DB、publish 給 SSE / webhook。斷線重連，退避。</summary>
public sealed class TrouterBackgroundService(
    TrouterClient trouter,
    MessageIngestor ingestor,
    ILogger<TrouterBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = MinBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await trouter.RunAsync(OnMessageAsync, stoppingToken);
                backoff = MinBackoff; // 正常斷線，重連從頭退避
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 常見是還沒匯入 cookie（換不到 token），退避後再試
                logger.LogWarning(ex, "Trouter 連線中斷，{Backoff} 後重連", backoff);
                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }

    private async Task OnMessageAsync(IncomingMessage incoming, CancellationToken ct)
    {
        // 只收訂閱中的對話（Trouter 會推帳號所有對話）；其餘丟掉，不入庫
        if (!await ingestor.IsSubscribedAsync(incoming.Message.ConversationId, ct))
            return;
        await ingestor.StoreAsync(incoming.Message, incoming.RawJson, publish: true, ct);
    }
}
