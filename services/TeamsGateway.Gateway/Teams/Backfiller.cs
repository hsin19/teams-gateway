using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Teams;

/// <summary>把某個對話的歷史訊息回補進 DB。訂閱時補滿一週；之後（重啟）只補上次之後的缺口。給 Elasticsearch 歷史搜尋留料。</summary>
public sealed class Backfiller(
    ChatsvcClient chatsvc,
    MessageIngestor ingestor,
    IDbContextFactory<GatewayDbContext> factory,
    IOptions<GatewayOptions> options,
    ILogger<Backfiller> logger)
{
    private const int MaxMessages = 5000;

    public async Task BackfillAsync(string conversationId, CancellationToken ct = default)
    {
        var window = DateTimeOffset.UtcNow - TimeSpan.FromDays(options.Value.BackfillDays);

        // 已存最新一則的時間：有就只補它之後（重啟補缺口），沒有就補滿整個 window（首次訂閱）
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var newest = await db.MessageEvents
                .Where(m => m.ConversationId == conversationId)
                .MaxAsync(m => (DateTimeOffset?)m.ComposeTime, ct);
            if (newest is { } n && n > window)
                window = n;
        }

        var history = await chatsvc.GetHistoryAsync(conversationId, window, MaxMessages, ct);
        var stored = 0;
        foreach (var item in history)
            if (await ingestor.StoreAsync(item.Dto, item.Raw, publish: false, ct))
                stored++;

        logger.LogInformation("回補 {Conversation}：抓到 {Fetched} 則、新增 {Stored} 則（{Since:u} 起）",
            conversationId, history.Count, stored, window);
    }
}
