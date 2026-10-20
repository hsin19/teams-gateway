using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamsGateway.Contracts;

namespace TeamsGateway.Gateway.Teams;

/// <summary>
/// 個人版 Teams 的 chatsvc REST client（列對話、讀訊息、發話）。對照 spike/FINDINGS.md「讀取」「發話」。
/// token 從 <see cref="SkypeTokenProvider"/> 拿，401 時強制換一次再重試。
/// </summary>
public sealed class ChatsvcClient(HttpClient http, SkypeTokenProvider tokens, ILogger<ChatsvcClient> logger)
{
    private const string View = "msnp24Equivalent|supportsMessageProperties|supportsExtendedHistory";

    public async Task<IReadOnlyList<ConversationDto>> GetConversationsAsync(CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"conversations?view={View}&pageSize=500"), ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("conversations", out var convs))
            return [];

        var result = new List<ConversationDto>();
        foreach (var c in convs.EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            if (id is null)
                continue;
            var tp = c.TryGetProperty("threadProperties", out var p) ? p : default;
            result.Add(new ConversationDto(
                id,
                GetString(tp, "topic"),
                GetString(tp, "productThreadType"),
                ParseTime(GetString(Child(c, "lastMessage"), "composetime"))));
        }
        return result;
    }

    /// <summary>把 thread id（19:...）直接放行；否則當群名對 topic 做前綴 / 包含比對。找不到就丟例外。</summary>
    public async Task<string> ResolveChatAsync(string spec, CancellationToken ct = default)
    {
        if (IsThreadId(spec))
            return spec;
        var hit = MatchByName(await GetConversationsAsync(ct), spec);
        if (hit is null)
            throw new InvalidOperationException($"找不到群 '{spec}'（名稱不符）");
        return hit.Id;
    }

    public static bool IsThreadId(string spec) =>
        spec.StartsWith("19:", StringComparison.Ordinal) || spec.Contains("@thread", StringComparison.Ordinal);

    /// <summary>群名對 topic 做前綴比對（優先）或包含比對，不分大小寫。</summary>
    public static ConversationDto? MatchByName(IReadOnlyList<ConversationDto> chats, string spec)
    {
        var low = spec.ToLowerInvariant();
        return chats.FirstOrDefault(c => c.Topic?.ToLowerInvariant().StartsWith(low) == true)
               ?? chats.FirstOrDefault(c => c.Topic?.ToLowerInvariant().Contains(low) == true);
    }

    public async Task<MessagePage> GetMessagesAsync(string threadId, int pageSize, string? cursor, CancellationToken ct = default)
    {
        var url = cursor ?? $"conversations/{Uri.EscapeDataString(threadId)}/messages?view={View}&pageSize={pageSize}&startTime=0";
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var messages = new List<MessageDto>();
        if (doc.RootElement.TryGetProperty("messages", out var arr))
        {
            foreach (var m in arr.EnumerateArray())
            {
                var parsed = ParseMessage(m, threadId);
                if (parsed is not null)
                    messages.Add(parsed);
            }
        }
        var next = doc.RootElement.TryGetProperty("_metadata", out var meta) ? GetString(meta, "backwardLink") : null;
        return new MessagePage(messages, next);
    }

    public async Task<SendMessageResult> SendMessageAsync(string threadId, string text, string displayName, CancellationToken ct = default)
    {
        var clientMessageId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var body = new
        {
            content = text,
            messagetype = "Text",
            contenttype = "text",
            clientmessageid = clientMessageId,
            imdisplayname = displayName,
        };
        using var resp = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"conversations/{Uri.EscapeDataString(threadId)}/messages") { Content = JsonContent.Create(body) },
            ct);
        return new SendMessageResult(clientMessageId);
    }

    /// <summary>static 版的訊息解析，給 Trouter 推播事件共用。</summary>
    public static MessageDto? ParseMessage(JsonElement m, string conversationId)
    {
        var id = GetString(m, "id");
        if (id is null)
            return null;
        return new MessageDto(
            id,
            conversationId,
            Mri(GetString(m, "from")),
            GetString(m, "imdisplayname"),
            GetString(m, "messagetype"),
            GetString(m, "content"),
            GetInt64(m, "sequenceId"),
            ParseTime(GetString(m, "composetime")) ?? DateTimeOffset.UtcNow);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        var token = await tokens.GetAsync(ct);
        var resp = await http.SendAsync(Apply(make(), token.Token), ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            logger.LogInformation("chatsvc 回 401，強制換 token 重試");
            resp.Dispose();
            token = await tokens.RefreshAsync(ct);
            resp = await http.SendAsync(Apply(make(), token.Token), ct);
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            throw new InvalidOperationException($"chatsvc {(int)resp.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
        }
        return resp;
    }

    private static HttpRequestMessage Apply(HttpRequestMessage req, string token)
    {
        req.Headers.TryAddWithoutValidation("x-skypetoken", token);
        req.Headers.TryAddWithoutValidation("authentication", $"skypetoken={token}");
        req.Headers.TryAddWithoutValidation("x-ms-client-type", "cdlworker");
        req.Headers.TryAddWithoutValidation("x-ms-client-version", "1415/26082021043");
        req.Headers.TryAddWithoutValidation("x-ms-user-type", "real-user");
        req.Headers.TryAddWithoutValidation("ms-ic3-product", "tfl");
        req.Headers.TryAddWithoutValidation("ms-ic3-additional-product", "Sfl");
        req.Headers.TryAddWithoutValidation("accept", "application/json");
        return req;
    }

    private static JsonElement Child(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var c) ? c : default;

    private static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetInt64(JsonElement e, string name)
    {
        // chatsvc 讀回來是數字，Trouter 推播也是數字，但偶爾是字串，兩種都接
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), out var n) => n,
            _ => 0,
        };
    }

    private static string? Mri(string? from) => from?.Split('/').LastOrDefault();

    private static DateTimeOffset? ParseTime(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
}
