namespace TeamsGateway.SlackRelay;

/// <summary>
/// relay 自己的 Slack bot 是誰（<c>auth.test</c>），第一次用到才查、查到就記住，查不到下次再查。
/// user id 認 @bot 與 bot 按的狀態表情；bot id 認頻道裡哪些訊息是自己貼的：同一個頻道可能有別的 app
/// （例如本機開發用的另一個 bot）也夾著同樣格式的 metadata，只認自己的，才不會把別人轉的當成自己轉的。
/// </summary>
public sealed class SlackBotIdentity(SlackClient slack)
{
    private BotIdentity? _identity;

    public async Task<BotIdentity?> GetAsync(CancellationToken ct) => _identity ??= await slack.GetBotIdentityAsync(ct);

    /// <summary>自己的 bot id；查不到（或 Slack 沒給）回 null，讀 metadata 時就不分是誰貼的（跟頻道裡只有一個 bot 時一樣）。</summary>
    public async Task<string?> BotIdAsync(CancellationToken ct) => (await GetAsync(ct))?.BotId;
}

/// <summary>bot 的 user id（<c>U…</c>）與 bot id（<c>B…</c>，它貼的訊息上的 <c>bot_id</c>）。</summary>
public readonly record struct BotIdentity(string UserId, string? BotId);
