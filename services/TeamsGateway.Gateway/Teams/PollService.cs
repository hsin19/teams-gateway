using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Teams;

/// <summary>
/// 輪詢保險：每 <c>PollSeconds</c> 抓一次各訂閱對話最近的訊息，把 Trouter 沒推到的新訊息補進來並 publish。
/// 補到新訊息就代表 Trouter 漏接，記 warning。最近 <see cref="Grace"/> 內的訊息先留給 Trouter，
/// 不然輪詢可能比推播早一步抓到、造成誤報；真的漏了下一輪會補到。
/// </summary>
public sealed class PollService(
    IServiceProvider services,
    IOptions<GatewayOptions> options,
    ILogger<PollService> logger) : BackgroundService
{
    private static readonly TimeSpan Lookback = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);
    private const int PageSize = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = options.Value.PollSeconds;
        if (seconds <= 0)
            return;
        var interval = TimeSpan.FromSeconds(seconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            // 先等一個 interval：重啟後讓 SSE 訂閱者先重連，補到的才有人收
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await PollAllAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "輪詢失敗");
            }
        }
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GatewayDbContext>>();
        var chatsvc = scope.ServiceProvider.GetRequiredService<ChatsvcClient>();
        var ingestor = scope.ServiceProvider.GetRequiredService<MessageIngestor>();

        List<string> ids;
        await using (var db = await factory.CreateDbContextAsync(ct))
            ids = await db.Conversations.Where(c => c.Subscribed).Select(c => c.Id).ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var id in ids)
        {
            var recent = await chatsvc.GetHistoryAsync(id, now - Lookback, maxMessages: 100, ct, PageSize);
            foreach (var item in recent.Where(h => h.Dto.Time < now - Grace).OrderBy(h => h.Dto.Time))
            {
                if (await ingestor.UpsertAsync(item.Dto, item.Raw, publish: true, ct) is IngestResult.Inserted)
                    logger.LogWarning("Trouter 漏接，輪詢補到：{Conversation} 訊息 {MessageId}（{Time:u}，{From}）",
                        id, item.Dto.Id, item.Dto.Time, item.Dto.FromName);
            }
        }
    }
}
