namespace TeamsGateway.SlackRelay;

/// <summary>
/// relay 自己的 Slack bot 是誰（<c>auth.test</c>），查到就記住。
/// user id 認 @bot 與 bot 按的狀態表情；bot id 認頻道裡哪些訊息是自己貼的：同一個頻道可能有別的 app
/// （例如本機開發用的另一個 bot）也夾著同樣格式的 metadata，只認自己的，才不會把別人轉的當成自己轉的。
/// </summary>
public sealed class SlackBotIdentity(SlackClient slack, ILogger<SlackBotIdentity> logger)
{
    // reference type：別的執行緒看到非 null 時兩個欄位一定都寫好了（struct 的寫入不是一次完成）
    private BotIdentity? _identity;

    /// <summary>
    /// 查不到就丟例外（讓呼叫端重試或回報），不回 null：讀 metadata 的過濾要是因為一時查不到就關掉，
    /// 等於又把別的 bot 貼的當成自己的。Slack 回了 user id 卻沒給 bot id 時，<see cref="BotIdentity.BotId"/> 才是 null（不過濾）。
    /// </summary>
    public async Task<BotIdentity> RequireAsync(CancellationToken ct) => _identity ??= await slack.GetBotIdentityAsync(ct);

    /// <summary>查不到回 null：給認 @bot / 表情的地方用，查不到就當不是自己的（不會誤送）。</summary>
    public async Task<BotIdentity?> GetAsync(CancellationToken ct)
    {
        try
        {
            return await RequireAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Slack auth.test failed; can't tell which bot this is yet");
            return null;
        }
    }
}

/// <summary>bot 的 user id（<c>U…</c>）與 bot id（<c>B…</c>，它貼的訊息上的 <c>bot_id</c>）。</summary>
public sealed record BotIdentity(string UserId, string? BotId);
