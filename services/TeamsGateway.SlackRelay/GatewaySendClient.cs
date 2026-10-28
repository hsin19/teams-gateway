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

    /// <summary>對話近期訊息裡帶這個 <paramref name="clientMessageId"/> 的那則的 Teams id；沒有回 null。gateway 不通就丟例外。</summary>
    public async Task<string?> FindSentAsync(string chat, string clientMessageId, CancellationToken ct)
    {
        var page = await http.GetFromJsonAsync($"/v1/chats/{Uri.EscapeDataString(chat)}/messages?limit=50",
            ContractsJsonContext.Default.MessagePage, ct);
        return page?.Messages.FirstOrDefault(m => m.ClientMessageId == clientMessageId)?.Id;
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
