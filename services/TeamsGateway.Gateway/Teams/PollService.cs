using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Teams;

/// <summary>
/// 輪詢保險：每 <c>PollSeconds</c> 抓一次各訂閱對話最近的訊息，把 Trouter 沒推到的新訊息補進來並 publish。
/// 補到新訊息就代表 Trouter 漏接，記 warning。最近 <see cref="Grace"/> 內的訊息先留給 Trouter，
/// 不然輪詢可能比推播早一步抓到、造成誤報；真的漏了下一輪會補到。
/// 對話的水位線（<see cref="Conversation.SyncedThrough"/>）落後超過 <see cref="Lookback"/>（停機 / 睡眠 / 剛訂閱失敗）時，
/// 這輪抓不到那段，改成整段回補並 publish。
/// </summary>
public sealed class PollService(
    IServiceProvider services,
    IOptions<GatewayOptions> options,
    ILogger<PollService> logger) : BackgroundService
{
    private static readonly TimeSpan Lookback = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
    private const int PageSize = 30;
    private const int MaxMessages = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = options.Value.PollSeconds;
        if (seconds <= 0)
            return;
        var interval = TimeSpan.FromSeconds(seconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            // 先等一個 interval：重啟後讓沒帶游標的 SSE 訂閱者先重連（帶 Last-Event-ID 的晚連上也會從事件 log 補回）
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

    /// <summary>水位線沒有、或比輪詢回看的範圍還舊：輪詢補不到中間那段，要整段回補。</summary>
    internal static bool NeedsBackfill(DateTimeOffset? syncedThrough, DateTimeOffset now) =>
        syncedThrough is not { } synced || synced < now - Lookback;

    private async Task PollAllAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GatewayDbContext>>();
        var chatsvc = scope.ServiceProvider.GetRequiredService<ChatsvcClient>();
        var ingestor = scope.ServiceProvider.GetRequiredService<MessageIngestor>();
        var backfiller = scope.ServiceProvider.GetRequiredService<Backfiller>();
        var metrics = scope.ServiceProvider.GetRequiredService<GatewayMetrics>();

        List<(string Id, DateTimeOffset? SyncedThrough)> convs;
        await using (var db = await factory.CreateDbContextAsync(ct))
            convs = (await db.Conversations.Where(c => c.Subscribed).Select(c => new { c.Id, c.SyncedThrough }).ToListAsync(ct))
                .Select(c => (c.Id, c.SyncedThrough)).ToList();

        var now = DateTimeOffset.UtcNow;
        foreach (var (id, syncedThrough) in convs)
        {
            // 一個對話壞掉（例如帳號被踢出群 → 403）不能讓排在後面的對話整輪都沒輪到
            try
            {
                await PollOneAsync(id, syncedThrough, now, factory, chatsvc, ingestor, backfiller, metrics, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "輪詢 {Conversation} 失敗", id);
            }
        }
    }

    private async Task PollOneAsync(
        string id, DateTimeOffset? syncedThrough, DateTimeOffset now, IDbContextFactory<GatewayDbContext> factory,
        ChatsvcClient chatsvc, MessageIngestor ingestor, Backfiller backfiller, GatewayMetrics metrics, CancellationToken ct)
    {
        if (NeedsBackfill(syncedThrough, now))
        {
            logger.LogInformation("{Conversation} 上次同步到 {Synced:u}，超過輪詢回看範圍，整段回補", id, syncedThrough);
            await backfiller.BackfillAsync(id, publishGap: true, ct);
            return;
        }

        var recent = await chatsvc.GetHistoryAsync(id, now - Lookback, MaxMessages, ct, PageSize);
        foreach (var item in recent.Where(h => h.Dto.Time < now - Grace).OrderBy(h => h.Dto.Time))
        {
            var result = await ingestor.UpsertAsync(item.Dto, item.Raw, publish: true, ct);
            metrics.Delivered(IngestSource.Poll, result, item.Dto);
            if (result is IngestResult.Inserted)
                logger.LogWarning("Trouter 漏接，輪詢補到：{Conversation} 訊息 {MessageId}（{Time:u}，{From}）",
                    id, item.Dto.Id, item.Dto.Time, item.Dto.FromName);
        }

        // 抓滿上限 = 回看範圍裡最舊那段可能沒抓到，水位線不動（落後超過回看時下一輪會整段回補）
        if (recent.Count < MaxMessages)
            await Backfiller.MarkSyncedAsync(factory, id, now - Grace, ct);
    }
}
