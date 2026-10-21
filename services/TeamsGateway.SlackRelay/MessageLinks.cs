using System.Collections.Concurrent;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// 記住每則轉過去的 Teams 訊息對到哪個 Slack 訊息（channel + ts + 當前文字），給：
/// (1) 回覆連回被引用訊息、(2) 編輯時原地更新 + 舊版進 thread 留證。
/// 行程記憶體：重啟後清空，對舊訊息的回覆 / 編輯就退回當新訊息處理。
/// </summary>
public sealed class MessageLinks
{
    private readonly ConcurrentDictionary<string, SlackRef> _byTeamsId = new();

    public void Remember(string teamsMessageId, string channel, string ts, string text) =>
        _byTeamsId[teamsMessageId] = new SlackRef(channel, ts, text);

    public SlackRef? Lookup(string teamsMessageId) =>
        _byTeamsId.TryGetValue(teamsMessageId, out var slackRef) ? slackRef : null;

    public readonly record struct SlackRef(string Channel, string Ts, string Text);
}
