using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TeamsGateway.SlackRelay;

/// <summary>Slack Web API：發訊息、取訊息 permalink（bot token，<c>chat:write</c>）。</summary>
public sealed class SlackClient(HttpClient http, ILogger<SlackClient> logger)
{
    /// <summary>發訊息，回傳貼成的訊息 ts（給之後連回引用用）；失敗回 null。</summary>
    public async Task<string?> PostMessageAsync(string channel, string text, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync("chat.postMessage", new { channel, text }, ct);
        // Slack 失敗也回 200，要看 body 的 ok
        var result = await resp.Content.ReadFromJsonAsync<PostResponse>(ct);
        if (result is { Ok: true })
            return result.Ts;

        logger.LogError("Slack chat.postMessage 失敗（channel={Channel}）：{Error}",
            channel, result?.Error ?? ((int)resp.StatusCode).ToString());
        return null;
    }

    /// <summary>在某則訊息的 thread 底下回一則（舊版留證用）。</summary>
    public async Task PostReplyAsync(string channel, string threadTs, string text, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync("chat.postMessage", new { channel, text, thread_ts = threadTs }, ct);
        var result = await resp.Content.ReadFromJsonAsync<PostResponse>(ct);
        if (result is not { Ok: true })
            logger.LogError("Slack thread 回覆失敗（channel={Channel}）：{Error}", channel, result?.Error ?? ((int)resp.StatusCode).ToString());
    }

    /// <summary>原地更新一則訊息（編輯時用；Slack 會自帶 "edited" 標記）。</summary>
    public async Task UpdateMessageAsync(string channel, string ts, string text, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync("chat.update", new { channel, ts, text }, ct);
        var result = await resp.Content.ReadFromJsonAsync<PostResponse>(ct);
        if (result is not { Ok: true })
            logger.LogError("Slack chat.update 失敗（channel={Channel}）：{Error}", channel, result?.Error ?? ((int)resp.StatusCode).ToString());
    }

    /// <summary>某則訊息的 permalink（貼進訊息裡 Slack 會 unfurl 成原生引用）；取不到回 null。</summary>
    public async Task<string?> GetPermalinkAsync(string channel, string ts, CancellationToken ct)
    {
        var url = $"chat.getPermalink?channel={Uri.EscapeDataString(channel)}&message_ts={Uri.EscapeDataString(ts)}";
        using var resp = await http.GetAsync(url, ct);
        var result = await resp.Content.ReadFromJsonAsync<PermalinkResponse>(ct);
        return result is { Ok: true } ? result.Permalink : null;
    }

    private sealed record PostResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("ts")] string? Ts,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record PermalinkResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("permalink")] string? Permalink,
        [property: JsonPropertyName("error")] string? Error);
}
