using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// Slack Web API：發訊息 / 原地更新 / thread 回覆 / 取 permalink / 加入頻道 / 讀歷史。
/// 轉過去的訊息會夾一個 metadata（Teams 訊息 id），讓重啟時能從 Slack 歷史重建對應。
/// </summary>
public sealed class SlackClient(HttpClient http, ILogger<SlackClient> logger)
{
    private const string MetaEventType = "teams_relay";

    private static object? Meta(string? teamsId) =>
        teamsId is null ? null : new { event_type = MetaEventType, event_payload = new { teams_id = teamsId } };

    /// <summary>發訊息（<paramref name="teamsId"/> 夾進 metadata）；回傳貼成的 ts，失敗 null。</summary>
    public async Task<string?> PostMessageAsync(string channel, string text, string? teamsId, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync("chat.postMessage", new { channel, text, metadata = Meta(teamsId) }, ct);
        var result = await resp.Content.ReadFromJsonAsync<PostResponse>(ct);
        if (result is { Ok: true })
            return result.Ts;
        logger.LogError("Slack chat.postMessage 失敗（channel={Channel}）：{Error}", channel, result?.Error ?? ((int)resp.StatusCode).ToString());
        return null;
    }

    /// <summary>原地更新一則訊息（編輯用；Slack 自帶 "edited"）。metadata 要一起帶，不然更新後會掉。</summary>
    public async Task UpdateMessageAsync(string channel, string ts, string text, string teamsId, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync("chat.update", new { channel, ts, text, metadata = Meta(teamsId) }, ct);
        var result = await resp.Content.ReadFromJsonAsync<PostResponse>(ct);
        if (result is not { Ok: true })
            logger.LogError("Slack chat.update 失敗（channel={Channel}）：{Error}", channel, result?.Error ?? ((int)resp.StatusCode).ToString());
    }

    /// <summary>在某則訊息的 thread 底下回一則（舊版留證用）。</summary>
    public async Task PostReplyAsync(string channel, string threadTs, string text, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync("chat.postMessage", new { channel, text, thread_ts = threadTs }, ct);
        var result = await resp.Content.ReadFromJsonAsync<PostResponse>(ct);
        if (result is not { Ok: true })
            logger.LogError("Slack thread 回覆失敗（channel={Channel}）：{Error}", channel, result?.Error ?? ((int)resp.StatusCode).ToString());
    }

    /// <summary>某則訊息的 permalink（貼進訊息裡 Slack 會 unfurl 成原生引用）；取不到回 null。</summary>
    public async Task<string?> GetPermalinkAsync(string channel, string ts, CancellationToken ct)
    {
        var url = $"chat.getPermalink?channel={Uri.EscapeDataString(channel)}&message_ts={Uri.EscapeDataString(ts)}";
        using var resp = await http.GetAsync(url, ct);
        var result = await resp.Content.ReadFromJsonAsync<PermalinkResponse>(ct);
        return result is { Ok: true } ? result.Permalink : null;
    }

    /// <summary>bot 加入頻道（才讀得到歷史；public 頻道用 <c>channels:join</c>）。已在裡面也回 ok。</summary>
    public async Task JoinChannelAsync(string channel, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync("conversations.join", new { channel }, ct);
        var result = await resp.Content.ReadFromJsonAsync<PostResponse>(ct);
        if (result is not { Ok: true })
            logger.LogWarning("Slack conversations.join 失敗（channel={Channel}）：{Error}", channel, result?.Error ?? ((int)resp.StatusCode).ToString());
    }

    /// <summary>讀頻道歷史，撈出我們轉過（帶 metadata）的訊息：(Teams id, ts, 當前文字)。往回翻到 <paramref name="oldestUnixSeconds"/>。</summary>
    public async Task<IReadOnlyList<RelayedMessage>> ReadRelayedAsync(string channel, long oldestUnixSeconds, CancellationToken ct)
    {
        var found = new List<RelayedMessage>();
        string? cursor = null;
        for (var page = 0; page < 20; page++) // 上限保護
        {
            var url = $"conversations.history?channel={Uri.EscapeDataString(channel)}&oldest={oldestUnixSeconds}&limit=200&include_all_metadata=true"
                      + (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var resp = await http.GetAsync(url, ct);
            var body = await resp.Content.ReadFromJsonAsync<HistoryResponse>(ct);
            if (body is not { Ok: true })
            {
                logger.LogWarning("Slack conversations.history 失敗（channel={Channel}）：{Error}", channel, body?.Error ?? ((int)resp.StatusCode).ToString());
                break;
            }
            foreach (var m in body.Messages ?? [])
                if (m.Metadata?.EventType == MetaEventType && m.Metadata.EventPayload?.TeamsId is { } tid && m.Ts is { } ts)
                    found.Add(new RelayedMessage(tid, ts, m.Text ?? ""));
            cursor = body.ResponseMetadata?.NextCursor;
            if (string.IsNullOrEmpty(cursor))
                break;
        }
        return found;
    }

    public readonly record struct RelayedMessage(string TeamsId, string Ts, string Text);

    private sealed record PostResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("ts")] string? Ts,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record PermalinkResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("permalink")] string? Permalink,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record HistoryResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("messages")] HistoryMessage[]? Messages,
        [property: JsonPropertyName("response_metadata")] ResponseMetadata? ResponseMetadata,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record HistoryMessage(
        [property: JsonPropertyName("ts")] string? Ts,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("metadata")] MessageMetadata? Metadata);

    private sealed record MessageMetadata(
        [property: JsonPropertyName("event_type")] string? EventType,
        [property: JsonPropertyName("event_payload")] MetadataPayload? EventPayload);

    private sealed record MetadataPayload(
        [property: JsonPropertyName("teams_id")] string? TeamsId);

    private sealed record ResponseMetadata(
        [property: JsonPropertyName("next_cursor")] string? NextCursor);
}
