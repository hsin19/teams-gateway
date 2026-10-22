using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Teams;

/// <summary>
/// 把某個對話的歷史訊息回補進 DB。訂閱時補滿一週；之後（重啟 / 睡眠後）從 <see cref="Conversation.SyncedThrough"/> 補缺口。
/// 給 Elasticsearch 歷史搜尋留料。
/// </summary>
public sealed class Backfiller(
    ChatsvcClient chatsvc,
    MessageIngestor ingestor,
    GatewayMetrics metrics,
    IDbContextFactory<GatewayDbContext> factory,
    IOptions<GatewayOptions> options,
    ILogger<Backfiller> logger)
{
    private const int MaxMessages = 5000;

    /// <summary>剛發的訊息 chatsvc 歷史可能還讀不到：水位線往回留這麼多，下次重疊抓（upsert 會去重）。</summary>
    internal static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);

    /// <param name="publishGap">
    /// 補「上次之後的缺口」時要不要 publish 給訂閱者。重啟補缺口要（那些是停機時漏掉的即時訊息，不發就永遠不會轉出去）；
    /// 首次訂閱補滿 window 一律不發（那是歷史，不該洗進訂閱者）。
    /// </param>
    public async Task BackfillAsync(string conversationId, bool publishGap, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var window = startedAt - TimeSpan.FromDays(options.Value.BackfillDays);
        var (since, isGap) = await ResolveSinceAsync(factory, conversationId, window, ct);

        var publish = publishGap && isGap;
        var history = await chatsvc.GetHistoryAsync(conversationId, since, MaxMessages, ct);
        var stored = 0;
        // GetHistoryAsync 是新→舊；照時間由舊到新寫入 / 發出，訂閱者那邊才不會順序顛倒
        foreach (var item in history.OrderBy(h => h.Dto.Time))
        {
            var result = await ingestor.UpsertAsync(item.Dto, item.Raw, publish, ct);
            if (result is IngestResult.Unchanged)
                continue;
            stored++;
            if (publish) // 首次訂閱的歷史只存不發，不算進「送達」
                metrics.Delivered(IngestSource.Backfill, result, item.Dto);
            if (publish && result is IngestResult.Inserted) // 補缺口抓到的新訊息 = 停機期間漏接
                logger.LogWarning("停機期間漏接，回補補到：{Conversation} 訊息 {MessageId}（{Time:u}，{From}）",
                    conversationId, item.Dto.Id, item.Dto.Time, item.Dto.FromName);
        }

        // 抓滿上限 = 最舊那段可能沒抓到，水位線不動，下次再從同一個位置補
        if (history.Count < MaxMessages)
            await MarkSyncedAsync(factory, conversationId, startedAt - SettleTime, ct);
        else
            logger.LogWarning("回補 {Conversation} 抓滿 {Max} 則上限，{Since:u} 之後可能還有沒補到的", conversationId, MaxMessages, since);

        logger.LogInformation("回補 {Conversation}：抓到 {Fetched} 則、新增 / 更新 {Stored} 則（{Since:u} 起）",
            conversationId, history.Count, stored, since);
    }

    /// <summary>
    /// 從哪裡開始補：有水位線（而且在 window 內）就從水位線補缺口，否則補滿 window、只存不發（從沒同步過 = 首次訂閱）。
    /// 不能用「已存最新一則的時間」：Trouter / 輪詢可能先存了一則新的，中間的缺口就被跳過了。
    /// </summary>
    internal static async Task<(DateTimeOffset Since, bool IsGap)> ResolveSinceAsync(
        IDbContextFactory<GatewayDbContext> factory, string conversationId, DateTimeOffset window, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var synced = await db.Conversations.Where(c => c.Id == conversationId).Select(c => c.SyncedThrough).FirstOrDefaultAsync(ct);
        return synced is { } s && s > window ? (s, true) : (window, false);
    }

    /// <summary>記下這個對話已完整同步到 <paramref name="through"/>；只往前推。</summary>
    internal static async Task MarkSyncedAsync(
        IDbContextFactory<GatewayDbContext> factory, string conversationId, DateTimeOffset through, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conv = await db.Conversations.FindAsync([conversationId], ct);
        if (conv is null || conv.SyncedThrough >= through)
            return;
        conv.SyncedThrough = through;
        await db.SaveChangesAsync(ct);
    }
}
