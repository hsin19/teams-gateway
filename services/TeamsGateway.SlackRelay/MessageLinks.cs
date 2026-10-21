using System.Collections.Concurrent;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// 記住每則轉過去的 Teams 訊息對到哪個 Slack 訊息（channel + ts），給「回覆連回被引用訊息」用。
/// 行程記憶體：重啟後清空，對舊訊息的回覆就退回 blockquote 呈現。
/// </summary>
public sealed class MessageLinks
{
    private readonly ConcurrentDictionary<string, SlackRef> _byTeamsId = new();

    public void Remember(string teamsMessageId, string channel, string ts) =>
        _byTeamsId[teamsMessageId] = new SlackRef(channel, ts);

    public SlackRef? Lookup(string teamsMessageId) =>
        _byTeamsId.TryGetValue(teamsMessageId, out var slackRef) ? slackRef : null;

    public readonly record struct SlackRef(string Channel, string Ts);
}
