using System.Net.Http.Json;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay;

/// <summary>經 gateway 發到 Teams（<c>POST /v1/chats/{id}/messages</c>），以及結果不明時到對話裡找那則有沒有送到（ADR 0012）。</summary>
public sealed class GatewaySendClient(HttpClient http, ILogger<GatewaySendClient> logger)
{
    /// <summary>
    /// 發一則。gateway 回 4xx = 請求本身有問題，確定沒送（<see cref="SendStatus.Rejected"/>）；
    /// 逾時、5xx、連不上都可能其實送到了（<see cref="SendStatus.Unknown"/>），呼叫端要先 <see cref="FindSentAsync"/> 才能重送。
    /// </summary>
    public async Task<SendOutcome> SendAsync(string chat, SendMessageRequest request, CancellationToken ct)
    {
        try
        {
            using var resp = await http.PostAsJsonAsync($"/v1/chats/{Uri.EscapeDataString(chat)}/messages", request,
                ContractsJsonContext.Default.SendMessageRequest, ct);
            if (resp.IsSuccessStatusCode)
            {
                var result = await resp.Content.ReadFromJsonAsync(ContractsJsonContext.Default.SendMessageResult, ct);
                return new SendOutcome(SendStatus.Sent, result?.MessageId, null);
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            var error = $"HTTP {(int)resp.StatusCode}{(body.Length > 0 ? $": {(body.Length > 200 ? body[..200] : body)}" : "")}";
            return new SendOutcome((int)resp.StatusCode is >= 400 and < 500 ? SendStatus.Rejected : SendStatus.Unknown, null, error);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Sending to Teams chat {Chat} got no answer from the gateway", chat);
            return new SendOutcome(SendStatus.Unknown, null, ex.Message);
        }
    }

    /// <summary>最多翻幾頁（每頁 200 則）；翻完還沒到那則 Slack 訊息寫下的時間就算查不完。</summary>
    internal const int MaxSearchPages = 40;

    /// <summary>
    /// 對話裡帶這個 <paramref name="clientMessageId"/> 的那則的 Teams id；確定沒有回 null。
    /// 只翻到那則 Slack 訊息寫下之前（clientMessageId 就是 Slack ts，送出不可能比它早，見 <see cref="SendTracker.EarliestSend"/>），
    /// 或翻到對話開頭（沒有下一頁、空頁）。gateway 不通、游標重複、翻了 <see cref="MaxSearchPages"/> 頁還沒到都丟例外：
    /// 呼叫端要當成結果不明，不能當成確定沒送而重送（Teams 不會去重）。
    /// </summary>
    public async Task<string?> FindSentAsync(string chat, string clientMessageId, CancellationToken ct)
    {
        // 停止點看 version 不看 composetime：chatsvc 的排序沒驗證過，最近被改的舊訊息可能排在前面；version 一定不小於 id
        var notBefore = SendTracker.EarliestSend(clientMessageId)?.ToUnixTimeMilliseconds();
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        for (var pages = 0; pages < MaxSearchPages; pages++)
        {
            var url = $"/v1/chats/{Uri.EscapeDataString(chat)}/messages?limit=200"
                      + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await http.GetFromJsonAsync(url, ContractsJsonContext.Default.MessagePage, ct)
                       ?? throw new InvalidDataException("The gateway returned an empty message page.");
            if (page.Messages.FirstOrDefault(m => m.ClientMessageId == clientMessageId) is { } sent)
                return sent.Id;
            if (page.NextCursor is null || page.Messages.Count == 0
                || (notBefore is { } cutoff && page.Messages.Any(m => m.Version > 0 && m.Version < cutoff)))
                return null;
            if (!seenCursors.Add(page.NextCursor))
                throw new InvalidDataException("The gateway repeated a message-page cursor.");
            cursor = page.NextCursor;
        }
        throw new InvalidDataException($"Searched {MaxSearchPages} pages without reaching the time the Slack message was written.");
    }
}

public enum SendStatus
{
    Sent,
    Rejected,
    Unknown,
}

/// <summary>發話結果。<c>MessageId</c> 是 Teams 給新訊息的 id（gateway 沒回時是 null）；<c>Error</c> 是給人看的失敗原因。</summary>
public readonly record struct SendOutcome(SendStatus Status, string? MessageId, string? Error);
