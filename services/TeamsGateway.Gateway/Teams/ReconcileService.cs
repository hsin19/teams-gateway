using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Teams;

/// <summary>
/// 慢更新：定期重掃訂閱對話近期的訊息，把漏接的編輯 / 刪除補進 DB（推播不一定會把每個編輯推來）。
/// 靠 version upsert——同 id 但 version 更大才更新，所以重掃很便宜。
/// </summary>
public sealed class ReconcileService(
    IServiceProvider services,
    IOptions<GatewayOptions> options,
    ILogger<ReconcileService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.Value.ReconcileMinutes;
        if (minutes <= 0)
            return;
        var interval = TimeSpan.FromMinutes(minutes);

        if (!await DelayAsync(interval, stoppingToken)) // 第一輪也等一個 interval，讓啟動回補先跑
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAllAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "慢更新失敗");
            }
            if (!await DelayAsync(interval, stoppingToken))
                break;
        }
    }

    private async Task ReconcileAllAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GatewayDbContext>>();
        var chatsvc = scope.ServiceProvider.GetRequiredService<ChatsvcClient>();
        var ingestor = scope.ServiceProvider.GetRequiredService<MessageIngestor>();

        var window = DateTimeOffset.UtcNow - TimeSpan.FromHours(options.Value.ReconcileWindowHours);
        List<string> ids;
        await using (var db = await factory.CreateDbContextAsync(ct))
            ids = await db.Conversations.Where(c => c.Subscribed).Select(c => c.Id).ToListAsync(ct);

        foreach (var id in ids)
        {
            var history = await chatsvc.GetHistoryAsync(id, window, maxMessages: 1000, ct);
            var changed = 0;
            // GetHistoryAsync 是新→舊；補回 / 重發要照時間由舊到新，Slack 才不會順序顛倒
            foreach (var item in history.OrderBy(h => h.Dto.Time))
                if (await ingestor.UpsertAsync(item.Dto, item.Raw, publish: true, ct) is IngestResult.Updated)
                    changed++;
            if (changed > 0)
                logger.LogInformation("慢更新 {Conversation}：{Changed} 則編輯 / 刪除", id, changed);
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
