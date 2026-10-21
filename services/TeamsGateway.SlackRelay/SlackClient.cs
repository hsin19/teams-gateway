using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TeamsGateway.SlackRelay;

/// <summary>Slack Web API 的 <c>chat.postMessage</c>（bot token，<c>chat:write</c>）。</summary>
public sealed class SlackClient(HttpClient http, ILogger<SlackClient> logger)
{
    public async Task<bool> PostMessageAsync(string channel, string text, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync("chat.postMessage", new { channel, text }, ct);
        // Slack 失敗也回 200，要看 body 的 ok
        var result = await resp.Content.ReadFromJsonAsync<SlackResponse>(ct);
        if (result is { Ok: true })
            return true;

        logger.LogError("Slack chat.postMessage 失敗（channel={Channel}）：{Error}",
            channel, result?.Error ?? ((int)resp.StatusCode).ToString());
        return false;
    }

    private sealed record SlackResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("error")] string? Error);
}
