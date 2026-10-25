using Microsoft.EntityFrameworkCore;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Teams;

/// <summary>
/// 啟動時對每個訂閱中的對話回補一次，補上停機期間漏掉的訊息並 publish 給訂閱者（也部分蓋掉 Trouter 重連空窗）。
/// </summary>
public sealed class StartupBackfillService(IServiceProvider services, ILogger<StartupBackfillService> logger) : BackgroundService
{
    // 補到的缺口要 publish。帶 Last-Event-ID / afterMessage 重連的訂閱者（relay）晚連上也會從事件 log 補回；
    // 等這一下是給沒帶游標、從「現在」開始收的訂閱者（relay 退避上限 30s）。
    // 輪詢開著時，它第一輪就會發現水位線落後、先整段回補，這裡只會從推進後的水位線再抓一小段（upsert 去重）；
    // 輪詢關掉（PollSeconds=0）時，重啟的缺口只靠這裡補
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
