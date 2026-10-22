using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Teams;

/// <summary>把某個對話的歷史訊息回補進 DB。訂閱時補滿一週；之後（重啟）只補上次之後的缺口。給 Elasticsearch 歷史搜尋留料。</summary>
public sealed class Backfiller(
    ChatsvcClient chatsvc,
    MessageIngestor ingestor,
    GatewayMetrics metrics,
    IDbContextFactory<GatewayDbContext> factory,
    IOptions<GatewayOptions> options,
    ILogger<Backfiller> logger)
{
    private const int MaxMessages = 5000;

    /// <param name="publishGap">
    /// 補「上次之後的缺口」時要不要 publish 給訂閱者。重啟補缺口要（那些是停機時漏掉的即時訊息，不發就永遠不會轉出去）；
    /// 首次訂閱補滿 window 一律不發（那是歷史，不該洗進訂閱者）。
    /// </param>
    public async Task BackfillAsync(string conversationId, bool publishGap, CancellationToken ct = default)
    {
        var window = DateTimeOffset.UtcNow - TimeSpan.FromDays(options.Value.BackfillDays);

        // 已存最新一則的時間：有就只補它之後（重啟補缺口），沒有就補滿整個 window（首次訂閱）
        var isGap = false;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var newest = await db.MessageEvents
                .Where(m => m.ConversationId == conversationId)
                .MaxAsync(m => (DateTimeOffset?)m.ComposeTime, ct);
            if (newest is { } n && n > window)
            {
                window = n;
                isGap = true;
            }
        }

        var publish = publishGap && isGap;
        var history = await chatsvc.GetHistoryAsync(conversationId, window, MaxMessages, ct);
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
                logger.LogWarning("停機期間漏接，啟動回補補到：{Conversation} 訊息 {MessageId}（{Time:u}，{From}）",
                    conversationId, item.Dto.Id, item.Dto.Time, item.Dto.FromName);
        }

        logger.LogInformation("回補 {Conversation}：抓到 {Fetched} 則、新增 / 更新 {Stored} 則（{Since:u} 起）",
            conversationId, history.Count, stored, window);
    }
}
