using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// 從 Slack 發到 Teams 的那幾則（ADR 0012）。送出前登記 clientMessageId → 那則 Slack 訊息，Teams 推回來時才認得出是自己發的
/// （推播可能比 API 回應早到）；那則上的狀態表情（⏳ 送出中 / 已送出 / ⚠️ 失敗）也在這裡換。
/// 發話（<see cref="SlackSender"/>）與轉送（<see cref="RelayWorker"/>）兩條路同時用，狀態都要能並行存取。
/// </summary>
public sealed class SendTracker(SlackClient slack, SlackBotIdentity bot, MessageLinks links, IOptions<RelayOptions> options, ILogger<SendTracker> logger)
{
    public const string DefaultSentEmoji = "outbox_tray";
    internal const string SendingEmoji = "hourglass_flowing_sand";
    internal const string FailedEmoji = "warning";

    /// <summary>clientMessageId 換回的 ts 最多可以比 Teams 訊息早多久（從 @bot 到按 Send 的間隔）；更早的當成 Teams client 自己的亂數。</summary>
    internal static readonly TimeSpan MaxSendDelay = TimeSpan.FromHours(24);

    // 兩邊的時鐘可能差一點
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    // 登記的留多久：推回來通常不到一秒；留久一點是為了 SSE 重送時還認得。過期的推回來還是能靠表情認出來
    private static readonly TimeSpan PendingTtl = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, PendingSend> _pending = new();
    private bool _customSentEmojiUnknown;

    /// <summary>有沒有開發話（設了 <c>Relay:Slack:AppToken</c>）。沒開時推回來的不必去 Slack 查表情。</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.Slack.AppToken);

    /// <summary>bot 自己的 user id；查不到回 null（見 <see cref="SlackBotIdentity"/>）。</summary>
    public async Task<string?> BotUserIdAsync(CancellationToken ct) => (await bot.GetAsync(ct))?.UserId;

    /// <summary>Slack ts → clientMessageId：去掉小數點（<c>1729834567.123456</c> → <c>1729834567123456</c>）。</summary>
    public static string ClientMessageId(string ts) => ts.Replace(".", "", StringComparison.Ordinal);

    /// <summary>
    /// clientMessageId → 可能是哪一則 Slack 訊息的 ts；不像就回 null。Teams 自己的 client 產生的 id 也有 16 碼，長度分不開，
    /// 所以換回的時間要落在 Teams 訊息之前 <see cref="MaxSendDelay"/> 內才算，亂數幾乎不會剛好落進來。只做字串處理，不經過浮點數。
    /// </summary>
    public static string? TsFromClientMessageId(string? clientMessageId, DateTimeOffset messageTime)
    {
        if (clientMessageId is not { Length: 16 } || !clientMessageId.All(char.IsAsciiDigit))
            return null;
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(clientMessageId[..10], CultureInfo.InvariantCulture));
        if (sentAt > messageTime + ClockSkew || sentAt < messageTime - MaxSendDelay)
            return null;
        return $"{clientMessageId[..10]}.{clientMessageId[10..]}";
    }

    /// <summary>
    /// 從這則 Slack 訊息發出的話，Teams 上最早可能出現的時間：clientMessageId 換回的 ts 減掉時鐘誤差。
    /// 到對話裡找那則時翻到比這更早就可以停。看不懂（不是 16 碼數字）回 null。
    /// </summary>
    internal static DateTimeOffset? EarliestSend(string clientMessageId) =>
        clientMessageId is { Length: 16 } && clientMessageId.All(char.IsAsciiDigit)
            ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(clientMessageId[..10], CultureInfo.InvariantCulture)) - ClockSkew
            : null;

    /// <summary>送出前登記；推回來時 <see cref="MatchAsync"/> 查得到。</summary>
    public PendingSend Register(string clientMessageId, string channel, string ts, bool inThread)
    {
        var cutoff = DateTimeOffset.UtcNow - PendingTtl;
        foreach (var (id, old) in _pending)
            if (old.RegisteredAt < cutoff)
                _pending.TryRemove(id, out _);
        return _pending[clientMessageId] = new PendingSend(channel, ts, inThread, DateTimeOffset.UtcNow);
    }

    /// <summary>記憶體裡登記過的那則（不問 Slack）；沒有回 null。</summary>
    public PendingSend? Pending(string? clientMessageId) =>
        clientMessageId is not null && _pending.TryGetValue(clientMessageId, out var send) ? send : null;

    /// <summary>
    /// Teams 推回來的這則是不是從 Slack 發的。先查記憶體；查不到（relay 重啟過）就把 clientMessageId 換回 ts，
    /// 看 <paramref name="channel"/> 裡那則上有沒有 bot 的狀態表情（重啟時可能還停在 ⏳；⚠️ 的其實送到了）。不是就回 null。
    /// </summary>
    public async Task<PendingSend?> MatchAsync(MessageDto message, string channel, CancellationToken ct)
    {
        if (message.ClientMessageId is not { } id)
            return null;
        if (_pending.TryGetValue(id, out var pending))
            return pending;
        if (!Enabled || TsFromClientMessageId(id, message.Time) is not { } ts)
            return null;
        if (await slack.GetReactionsAsync(channel, ts, ct) is not { } sent
            || await BotUserIdAsync(ct) is not { } bot
            || !HasStatus(sent, bot, includeFailed: true))
            return null;
        var inThread = sent.ThreadTs is { } threadTs && threadTs != ts;
        return _pending[id] = new PendingSend(channel, ts, inThread, DateTimeOffset.UtcNow);
    }

    /// <summary>那則上有沒有 bot 按的狀態表情：⏳、已送出，要的話加上 ⚠️。</summary>
    internal bool HasStatus(HistoryMessage message, string botUserId, bool includeFailed) =>
        IsSent(message, botUserId) || IsSending(message, botUserId) || (includeFailed && IsFailed(message, botUserId));

    /// <summary>bot 按了已送出表情（設定的與預設的都算，改過設定後之前發的照樣認得）。</summary>
    internal bool IsSent(HistoryMessage message, string botUserId) =>
        ByBot(message, botUserId, DefaultSentEmoji) || ByBot(message, botUserId, options.Value.Slack.SentEmoji);

    internal bool IsStatusEmoji(string name) =>
        name is SendingEmoji or FailedEmoji
        || name == DefaultSentEmoji
        || name == options.Value.Slack.SentEmoji;

    /// <summary>還停在 ⏳：正在送，或上次送到一半 relay 重啟 / Slack 出錯，不知道送了沒。</summary>
    internal static bool IsSending(HistoryMessage message, string botUserId) => ByBot(message, botUserId, SendingEmoji);

    /// <summary>上次標了 ⚠️：當時確定沒送，或結果不明時對話裡還查不到（之後才出現也可能）。</summary>
    internal static bool IsFailed(HistoryMessage message, string botUserId) => ByBot(message, botUserId, FailedEmoji);

    private static bool ByBot(HistoryMessage message, string botUserId, string emoji) =>
        message.Reactions?.Any(r => r.Name == emoji && r.Users?.Contains(botUserId) == true) == true;

    // 換表情都是先按新的再拿掉舊的：中間一步失敗（例如被限流）時那則至少還帶著一個狀態，防重送與重啟後的辨認都靠它

    public async Task MarkSendingAsync(string channel, string ts, CancellationToken ct)
    {
        await ReactStatusAsync(channel, ts, SendingEmoji, add: true, ct);
        await ReactStatusAsync(channel, ts, FailedEmoji, add: false, ct); // Retry 時上一次的 ⚠️
    }

    internal async Task<bool> TryMarkFailedAsync(PendingSend send, string clientMessageId, CancellationToken ct)
    {
        await send.CompletionGate.WaitAsync(ct);
        try
        {
            if (send.Confirmed || send.Delivered)
                return false;
            await ReactStatusAsync(send.Channel, send.Ts, FailedEmoji, add: true, ct);
            await ReactStatusAsync(send.Channel, send.Ts, SendingEmoji, add: false, ct);
            _pending.TryRemove(clientMessageId, out _);
            return true;
        }
        finally
        {
            send.CompletionGate.Release();
        }
    }

    /// <summary>
    /// Teams 收下了（API 回了 id，或推回來了）：狀態換成已送出；頂層發的那則記進 <see cref="MessageLinks"/>
    /// （Teams 那邊引用它、按表情時接得回來）。發話與推回兩邊都會叫，重複叫沒關係。
    /// </summary>
    public async Task ConfirmAsync(PendingSend send, string? teamsId, string? chat, CancellationToken ct)
    {
        // 先記住「送到了」：後面換表情失敗時，發話那邊的查詢就算查不到也不會把它標成失敗、說可以重送
        send.MarkDelivered();
        // 對應表先記、不管確認過沒：先確認的那邊（API 回應）可能沒帶 Teams id，推回來那次才補得上。
        // 版本記 null：之後有沒有被改看 Edited / Deleted 標記，不靠「版本 = id」這個假設
        if (!send.InThread && teamsId is not null && links.Lookup(teamsId) is null)
            links.Remember(teamsId, send.Channel, send.Ts, "", null, chat: chat, byBot: false);
        await send.CompletionGate.WaitAsync(ct);
        try
        {
            if (send.Confirmed)
                return;
            await AddSentEmojiAsync(send.Channel, send.Ts, ct);
            await ReactStatusAsync(send.Channel, send.Ts, SendingEmoji, add: false, ct);
            await ReactStatusAsync(send.Channel, send.Ts, FailedEmoji, add: false, ct);
            send.SetConfirmed();
        }
        finally
        {
            send.CompletionGate.Release();
        }
    }

    // 📤 是固定標記，一律先按：防重送與重啟後的辨認都靠它，改了 SentEmoji 之前發的照樣認得。
    // 自訂的只是顯示：打錯或還沒上傳（invalid_name）就之後不再試，其他失敗也只記 warning，不能讓已送出的那則卡住
    private async Task AddSentEmojiAsync(string channel, string ts, CancellationToken ct)
    {
        await ReactStatusAsync(channel, ts, DefaultSentEmoji, add: true, ct);
        var name = options.Value.Slack.SentEmoji;
        if (name == DefaultSentEmoji || _customSentEmojiUnknown)
            return;
        try
        {
            if (await slack.ReactAsync(channel, ts, name, add: true, ct))
                return;
            logger.LogWarning("Slack doesn't know the emoji :{Emoji}: (Relay:Slack:SentEmoji); only :{Default}: is added", name, DefaultSentEmoji);
            _customSentEmojiUnknown = true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Adding the display emoji :{Emoji}: failed; :{Default}: is there", name, DefaultSentEmoji);
        }
    }

    private async Task ReactStatusAsync(string channel, string ts, string name, bool add, CancellationToken ct)
    {
        if (!await slack.ReactAsync(channel, ts, name, add, ct))
            throw new InvalidOperationException($"Slack doesn't know the send-status emoji :{name}:");
    }
}

/// <summary>
/// 一則從 Slack 發出的訊息：在哪個頻道、哪一則、是不是 thread 裡的回覆。
/// <c>Delivered</c> = Teams 收下了；<c>Confirmed</c> = 而且已經換成已送出表情。
/// </summary>
public sealed record PendingSend(string Channel, string Ts, bool InThread, DateTimeOffset RegisteredAt)
{
    private int _confirmed;
    private int _delivered;

    // 送到了（推回來或 API 回應）與送失敗（API 失敗、查不到）兩邊可能同時結束：同一則一次只讓一邊換狀態，
    // 後到的看到已確認就收手，不會把送到的那則標成失敗
    internal SemaphoreSlim CompletionGate { get; } = new(1, 1);

    public bool Confirmed => Volatile.Read(ref _confirmed) != 0;

    /// <summary>已經知道 Teams 收下了（API 回應或推回來），表情不一定換好了。</summary>
    public bool Delivered => Volatile.Read(ref _delivered) != 0;

    internal void SetConfirmed() => Volatile.Write(ref _confirmed, 1);

    internal void MarkDelivered() => Volatile.Write(ref _delivered, 1);
}
