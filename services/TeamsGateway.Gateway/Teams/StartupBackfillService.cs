using Microsoft.EntityFrameworkCore;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Teams;

/// <summary>
/// 啟動時對每個訂閱中的對話回補一次，補上停機期間漏掉的訊息並 publish 給訂閱者（也部分蓋掉 Trouter 重連空窗）。
/// </summary>
public sealed class StartupBackfillService(IServiceProvider services, ILogger<StartupBackfillService> logger) : BackgroundService
{
    // 補到的缺口要 publish：先等 SSE 訂閱者（relay 退避上限 30s）重連上來，不然發出去沒人收
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GatewayDbContext>>();
        var backfiller = scope.ServiceProvider.GetRequiredService<Backfiller>();

        List<string> ids;
        await using (var db = await factory.CreateDbContextAsync(stoppingToken))
            ids = await db.Conversations.Where(c => c.Subscribed).Select(c => c.Id).ToListAsync(stoppingToken);

        foreach (var id in ids)
        {
            try
            {
                await backfiller.BackfillAsync(id, publishGap: true, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "啟動回補 {Conversation} 失敗（可能還沒匯入 cookie）", id);
            }
        }
    }
}
