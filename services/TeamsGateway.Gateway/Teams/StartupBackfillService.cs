using Microsoft.EntityFrameworkCore;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Teams;

/// <summary>啟動時對每個訂閱中的對話回補一次，補上停機期間漏掉的訊息（也部分蓋掉 Trouter 重連空窗）。</summary>
public sealed class StartupBackfillService(IServiceProvider services, ILogger<StartupBackfillService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); // 等 app / token 就緒
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
                await backfiller.BackfillAsync(id, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "啟動回補 {Conversation} 失敗（可能還沒匯入 cookie）", id);
            }
        }
    }
}
