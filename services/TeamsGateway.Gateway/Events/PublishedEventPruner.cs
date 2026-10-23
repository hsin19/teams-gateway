using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Events;

/// <summary>
/// 定期清內部事件 log（ADR 0006）：只清超過 <c>EventRetentionDays</c>、而且所有啟用中的 sink 都送過的事件，
/// 下游掛幾天也不掉。訊息本體在 <c>MessageEvents</c>，log 不用永久留。
/// SSE 的游標在 client、gateway 不知道，斷線比保留期還久的訂閱者重連時就從現在開始收。
/// </summary>
public sealed class PublishedEventPruner(
    IDbContextFactory<GatewayDbContext> factory,
    IEnumerable<IEventSink> sinks,
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
                var deleted = await PruneAsync(factory, sinks, cutoff, stoppingToken);
                if (deleted > 0)
                    logger.LogInformation("清掉 {Count} 筆 {Cutoff:u} 之前、所有 sink 都送過的事件", deleted, cutoff);
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

    /// <summary>刪掉早於 <paramref name="cutoff"/>、序號又比每個 sink 還沒送到的都小的事件，回傳筆數。</summary>
    internal static async Task<int> PruneAsync(
        IDbContextFactory<GatewayDbContext> factory, IEnumerable<IEventSink> sinks, DateTimeOffset cutoff, CancellationToken ct)
    {
        long? floor = null;
        foreach (var sink in sinks)
            if (await sink.OldestPendingAsync(ct) is { } pending)
                floor = floor is { } f ? Math.Min(f, pending) : pending;

        await using var db = await factory.CreateDbContextAsync(ct);
        var old = db.PublishedEvents.Where(e => e.PublishedAt < cutoff);
        if (floor is { } keepFrom)
            old = old.Where(e => e.Seq < keepFrom);
        return await old.ExecuteDeleteAsync(ct);
    }
}
