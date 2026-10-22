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
    private readonly ConcurrentDictionary<string, SlackRef> _byTeamsId = new();

    public void Remember(string teamsMessageId, string channel, string ts, string text, long? version, IReadOnlyList<string>? images = null) =>
        _byTeamsId[teamsMessageId] = new SlackRef(channel, ts, text, version, images ?? []);

    public SlackRef? Lookup(string teamsMessageId) =>
        _byTeamsId.TryGetValue(teamsMessageId, out var slackRef) ? slackRef : null;

    public int Count => _byTeamsId.Count;

    /// <summary>
    /// <paramref name="Version"/>：目前顯示的是 Teams 哪一版（毫秒時間戳；這功能上線前轉的訊息回補回來是 null）。
    /// <paramref name="Images"/>：這則訊息 image block 引用的 Slack file id（重啟回補的只有文字，這裡會是空的）。
    /// </summary>
    public readonly record struct SlackRef(string Channel, string Ts, string Text, long? Version, IReadOnlyList<string> Images);
}
