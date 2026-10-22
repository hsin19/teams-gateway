using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Events;

/// <summary>
/// 定期刪掉超過 <c>EventRetentionDays</c> 的 <see cref="PublishedEvent"/>。它只給 SSE 斷線補收用，
/// 訊息本體在 <c>MessageEvents</c>，所以舊事件不用留；斷線比保留期還久的訂閱者重連時就從現在開始收。
/// </summary>
public sealed class PublishedEventPruner(
    IDbContextFactory<GatewayDbContext> factory,
    IOptions<GatewayOptions> options,
    ILogger<PublishedEventPruner> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(options.Value.EventRetentionDays);
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                var deleted = await db.PublishedEvents.Where(e => e.PublishedAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                if (deleted > 0)
                    logger.LogInformation("清掉 {Count} 筆 {Cutoff:u} 之前的已發事件", deleted, cutoff);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "清舊事件失敗");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
