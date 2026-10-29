using System.Collections.Concurrent;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// 記住每則轉過去的 Teams 訊息對到哪個 Slack 訊息（channel + ts + 當前文字 / 版本 + 附圖的 Slack file id），給：
/// (1) 回覆連回被引用訊息、(2) 編輯時原地更新 + 舊版（含圖）進 thread 留證。
/// 純記憶體；重啟時由 <c>RelayWorker</c> 讀各頻道近期歷史的 Slack message metadata（夾著 Teams id）重建，
/// 所以不用本機存檔——Slack 自己就是 source of truth。
/// </summary>
public sealed class MessageLinks
{
    /// <summary>記多久：比重啟回補（預設 24h）長，常駐時舊一點的訊息被編輯也能原地更新；再舊就放掉，記憶體不會一直長。</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private const int PruneEvery = 500;

    private readonly ConcurrentDictionary<string, SlackRef> _byTeamsId = new();
    private int _sincePrune;

    public void Remember(string teamsMessageId, string channel, string ts, string text, long? version, IReadOnlyList<string>? images = null,
        string? chat = null, bool byBot = true)
    {
        _byTeamsId[teamsMessageId] = new SlackRef(channel, ts, text, version, images ?? [], chat, byBot);
        if (Interlocked.Increment(ref _sincePrune) >= PruneEvery)
        {
            Interlocked.Exchange(ref _sincePrune, 0);
            PruneOlderThan(DateTimeOffset.UtcNow - MaxAge);
        }
    }

    /// <summary>丟掉 Slack 訊息時間（ts）早於 <paramref name="cutoff"/> 的對應。</summary>
    internal void PruneOlderThan(DateTimeOffset cutoff)
    {
        foreach (var (teamsId, slackRef) in _byTeamsId)
            if (PostedAt(slackRef.Ts) < cutoff)
                _byTeamsId.TryRemove(teamsId, out _);
    }

    /// <summary>
    /// 最後轉出去（Slack 上最新）的那則 Teams 訊息 id；重啟後沒有事件序號時，從它之後續接。
    /// 人從 Slack 發的那幾則不算（ADR 0012）：它們可能在續接點定下來前才記進來（剛啟動就按了 Send），
    /// 算進去的話會跳過停機期間 Teams 上的訊息。
    /// </summary>
    public string? NewestTeamsId()
    {
        string? newest = null;
        var newestAt = DateTimeOffset.MinValue;
        foreach (var (teamsId, slackRef) in _byTeamsId)
        {
            if (!slackRef.ByBot)
                continue;
            var at = PostedAt(slackRef.Ts);
            if (at != DateTimeOffset.MaxValue && at > newestAt)
                (newest, newestAt) = (teamsId, at);
        }
        return newest;
    }

    /// <summary>Slack ts（<c>1729000000.000100</c>）是 unix 秒；看不懂就當很新，不刪。</summary>
    private static DateTimeOffset PostedAt(string ts) =>
        double.TryParse(ts, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000))
            : DateTimeOffset.MaxValue;

    public SlackRef? Lookup(string teamsMessageId) =>
        _byTeamsId.TryGetValue(teamsMessageId, out var slackRef) ? slackRef : null;

    public int Count => _byTeamsId.Count;

    /// <summary>
    /// <paramref name="Version"/>：目前顯示的是 Teams 哪一版（毫秒時間戳；這功能上線前轉的訊息回補回來是 null）。
    /// <paramref name="Images"/>：這則訊息 image block 引用的 Slack file id（重啟回補的只有文字，這裡會是空的）。
    /// <paramref name="Chat"/>：Teams 訊息所在的對話（metadata 還沒帶這個欄位時轉的訊息是 null）。
    /// <paramref name="ByBot"/>：false = 對到的是人在 Slack 頂層發、送到 Teams 的那則（ADR 0012），bot 改不了它。
    /// </summary>
    public readonly record struct SlackRef(string Channel, string Ts, string Text, long? Version, IReadOnlyList<string> Images,
        string? Chat = null, bool ByBot = true);
}
