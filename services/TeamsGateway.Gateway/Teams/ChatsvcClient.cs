using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamsGateway.Contracts;

namespace TeamsGateway.Gateway.Teams;

/// <summary>
/// 個人版 Teams 的 chatsvc REST client（列對話、讀訊息、發話、改 / 刪自己的訊息）。對照 spike/FINDINGS.md「讀取」「發話」「編輯與刪除」。
/// token 從 <see cref="SkypeTokenProvider"/> 拿，401 時強制換一次再重試。
/// </summary>
public sealed class ChatsvcClient(HttpClient http, SkypeTokenProvider tokens, ILogger<ChatsvcClient> logger)
{
    private const string View = "msnp24Equivalent|supportsMessageProperties|supportsExtendedHistory";

    public async Task<IReadOnlyList<ConversationDto>> GetConversationsAsync(CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"conversations?view={View}&pageSize=500"), ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return ParseConversations(doc.RootElement);
    }

    public static IReadOnlyList<ConversationDto> ParseConversations(JsonElement root)
    {
        if (!root.TryGetProperty("conversations", out var convs))
            return [];

        var result = new List<ConversationDto>();
        foreach (var c in convs.EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            if (id is null)
                continue;
            var tp = c.TryGetProperty("threadProperties", out var p) ? p : default;
            // topic 是 HTML 跳脫過的（& → &amp;），不還原的話群名帶 & 的對話用名字找不到
            var topic = GetString(tp, "topic");
            result.Add(new ConversationDto(
                id,
                topic is null ? null : WebUtility.HtmlDecode(topic),
                GetString(tp, "productThreadType"),
                ParseTime(GetString(Child(c, "lastMessage"), "composetime"))));
        }
        return result;
    }

    /// <summary>把 thread id（19:...）直接放行；否則當群名比對（見 <see cref="MatchByName"/>）。找不到或對到不只一個就丟例外。</summary>
    public async Task<string> ResolveChatAsync(string spec, CancellationToken ct = default)
    {
        if (IsThreadId(spec))
            return spec;
        var hits = MatchByName(await GetConversationsAsync(ct), spec);
        return hits.Count switch
        {
            1 => hits[0].Id,
            0 => throw new InvalidOperationException($"No chat named '{spec}'"),
            _ => throw new InvalidOperationException(Ambiguous(spec, hits)),
        };
    }

    /// <summary>群名對到不只一個時的說明，列出候選讓呼叫端改給完整名稱或 id。</summary>
    public static string Ambiguous(string spec, IReadOnlyList<ConversationDto> hits) =>
        $"'{spec}' matches {hits.Count} chats: {string.Join(", ", hits.Take(10).Select(h => $"{h.Topic} ({h.Id})"))}; give a fuller name or the thread id";

    public static bool IsThreadId(string spec) =>
        spec.StartsWith("19:", StringComparison.Ordinal) || spec.Contains("@thread", StringComparison.Ordinal);

    /// <summary>
    /// 群名對 topic 比對，不分大小寫，依序：完全相同 → 前綴 → 包含，回第一個有結果那層的全部符合。
    /// 呼叫端只在剛好一個時採用：發話不能撤回，「dev」不能默默挑到「devops-alerts」。
    /// </summary>
    public static IReadOnlyList<ConversationDto> MatchByName(IReadOnlyList<ConversationDto> chats, string spec)
    {
        var low = spec.Trim().ToLowerInvariant();
        Func<string, bool>[] rules = [t => t == low, t => t.StartsWith(low, StringComparison.Ordinal), t => t.Contains(low, StringComparison.Ordinal)];
        foreach (var rule in rules)
        {
            var hits = chats.Where(c => c.Topic is { } topic && rule(topic.Trim().ToLowerInvariant())).ToList();
            if (hits.Count > 0)
                return hits;
        }
        return [];
    }

    /// <summary>一則訊息連同原始 JSON（給持久化留存）。</summary>
    public readonly record struct RawMessage(MessageDto Dto, string Raw);

    /// <param name="cursor">上一頁回的 <c>NextCursor</c>；外部傳進來的，必須先過 <see cref="IsValidCursor"/>。</param>
    public async Task<MessagePage> GetMessagesAsync(string threadId, int pageSize, string? cursor, CancellationToken ct = default)
    {
        if (cursor is not null && !IsValidCursor(threadId, cursor))
            throw new ArgumentException("cursor is not a paging cursor for this chat", nameof(cursor));
        var (items, next) = await FetchPageAsync(threadId, pageSize, cursor, ct);
        return new MessagePage(items.Select(i => i.Dto).ToList(), next);
    }

    // backwardLink 沒抓過非 base 主機的樣本；保險起見也收 Teams / Skype 的訊息主機（skypetoken 本來就會送去那些地方）
    private static readonly string[] CursorHostSuffixes = [".teams.microsoft.com", ".skype.com"];

    /// <summary>
    /// 翻頁游標就是 chatsvc 回的 backwardLink（絕對網址），會帶著 skypetoken 原樣打出去。
    /// 所以只收「同一個對話的 messages 端點、主機是 Teams / Skype」的網址——不然任何 API key 持有者
    /// 都能叫 gateway 把 token 送到自己的主機，或拿它打內網。
    /// </summary>
    public bool IsValidCursor(string threadId, string cursor) => IsCursorFor(http.BaseAddress!.Host, threadId, cursor);

    internal static bool IsCursorFor(string baseHost, string threadId, string cursor) =>
        Uri.TryCreate(cursor, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0
        && (uri.Host.Equals(baseHost, StringComparison.OrdinalIgnoreCase)
            || CursorHostSuffixes.Any(s => uri.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
        && Uri.UnescapeDataString(uri.AbsolutePath).EndsWith($"/users/ME/conversations/{threadId}/messages", StringComparison.Ordinal);

    /// <summary>往回翻頁抓歷史，直到訊息比 <paramref name="cutoff"/> 舊或到頂（或到 <paramref name="maxMessages"/> 上限）。給回補用。</summary>
    public async Task<IReadOnlyList<RawMessage>> GetHistoryAsync(string threadId, DateTimeOffset cutoff, int maxMessages, CancellationToken ct = default, int pageSize = 200)
    {
        var result = new List<RawMessage>();
        string? cursor = null;
        for (var page = 0; page < 40 && result.Count < maxMessages; page++)
        {
            var (items, next) = await FetchPageAsync(threadId, pageSize, cursor, ct);
            if (items.Count == 0)
                break;
            var reachedCutoff = false;
            foreach (var item in items)
            {
                if (item.Dto.Time is { } t && t < cutoff)
                    reachedCutoff = true;
                else if (item.Dto.Type is "Text" or "RichText/Html")
                    result.Add(item);
            }
            if (reachedCutoff || next is null)
                break;
            cursor = next;
        }
        return result;
    }

    private const int WindowPageSize = 200;
    private const int MaxWindowPages = 40;

    /// <summary>讀 [since, until) 內最新的 limit 則文字訊息（給 MCP）。什麼時候停見 <see cref="MessageWindowCollector"/>。</summary>
    public async Task<MessageWindow> GetWindowAsync(string threadId, DateTimeOffset? since, DateTimeOffset? until, int limit, CancellationToken ct = default)
    {
        var collector = new MessageWindowCollector(since, until, limit);
        string? cursor = null;
        for (var page = 0; page < MaxWindowPages; page++)
        {
            var (items, next) = await FetchPageAsync(threadId, WindowPageSize, cursor, ct);
            var done = collector.Add(items.Select(i => i.Dto).ToList());
            if (next is null || items.Count == 0)
                return collector.Build(reachedTop: true);
            if (done)
                return collector.Build(reachedTop: false);
            cursor = next;
        }
        return collector.Build(reachedTop: false);
    }

    /// <summary>
    /// 找一則訊息（給 MCP 看圖）。訊息 id 就是發話時間（Unix 毫秒），翻到 version 比它舊就表示不在了；
    /// 留一分鐘餘裕，不賭 id 跟 version 用的是同一個時鐘。
    /// </summary>
    public async Task<MessageDto?> FindMessageAsync(string threadId, string messageId, CancellationToken ct = default)
    {
        if (!long.TryParse(messageId, NumberStyles.None, CultureInfo.InvariantCulture, out var composedAt))
            return null;
        string? cursor = null;
        for (var page = 0; page < MaxWindowPages; page++)
        {
            var (items, next) = await FetchPageAsync(threadId, WindowPageSize, cursor, ct);
            foreach (var item in items)
                if (item.Dto.Id == messageId)
                    return item.Dto;
            if (next is null || items.Count == 0 || items.Any(i => i.Dto.Version > 0 && i.Dto.Version < composedAt - 60_000))
                return null;
            cursor = next;
        }
        return null;
    }

    private async Task<(List<RawMessage> Items, string? Next)> FetchPageAsync(string threadId, int pageSize, string? cursor, CancellationToken ct)
    {
        var url = cursor ?? $"conversations/{Uri.EscapeDataString(threadId)}/messages?view={View}&pageSize={pageSize}&startTime=0";
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var items = new List<RawMessage>();
        if (doc.RootElement.TryGetProperty("messages", out var arr))
        {
            foreach (var m in arr.EnumerateArray())
            {
                var parsed = ParseMessage(m, threadId);
                if (parsed is not null)
                    items.Add(new RawMessage(parsed, m.GetRawText()));
            }
        }
        var next = doc.RootElement.TryGetProperty("_metadata", out var meta) ? GetString(meta, "backwardLink") : null;
        return (items, next);
    }

    public Task<SendMessageResult> SendMessageAsync(string threadId, string text, string displayName, CancellationToken ct = default) =>
        SendMessageAsync(threadId, new OutgoingMessage(text), displayName, ct);

    public async Task<SendMessageResult> SendMessageAsync(string threadId, OutgoingMessage message, string displayName, CancellationToken ct = default)
    {
        // 亂數而不是時間戳：同一毫秒的兩個發話不能撞號（網頁版把同一個 id 的訊息畫成一則）
        var clientMessageId = message.ClientMessageId
                              ?? Random.Shared.NextInt64(1_000_000_000_000_000_000, long.MaxValue).ToString(CultureInfo.InvariantCulture);
        var body = SendBody(message, clientMessageId, displayName);
        using var resp = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"conversations/{Uri.EscapeDataString(threadId)}/messages")
            {
                Content = JsonContent.Create(body, TeamsJsonContext.Default.ChatsvcSendRequest),
            },
            ct);
        return new SendMessageResult(clientMessageId, SentMessageId(await resp.Content.ReadAsStringAsync(ct), resp.Headers.Location));
    }

    /// <summary>
    /// 改綁定帳號自己發的那則：整則換掉，原本是引用回覆的話引用要再帶（<see cref="OutgoingMessage.Quote"/>）。
    /// 不回新版本，編輯會照常推回來。刪掉的 → <see cref="ChatsvcException"/> 403 <c>MessageAlreadyDeleted</c>；沒有這則 → 404。
    /// </summary>
    public async Task EditMessageAsync(string threadId, string messageId, OutgoingMessage message, string displayName, CancellationToken ct = default)
    {
        var body = SendBody(message, clientMessageId: null, displayName);
        using var _ = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"conversations/{Uri.EscapeDataString(threadId)}/messages/{Uri.EscapeDataString(messageId)}")
            {
                Content = JsonContent.Create(body, TeamsJsonContext.Default.ChatsvcSendRequest),
            },
            ct);
    }

    /// <summary>刪掉綁定帳號自己發的那則。刪過的再刪一樣成功；沒有這則 → <see cref="ChatsvcException"/> 404。</summary>
    public async Task DeleteMessageAsync(string threadId, string messageId, CancellationToken ct = default)
    {
        using var _ = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"conversations/{Uri.EscapeDataString(threadId)}/messages/{Uri.EscapeDataString(messageId)}"),
            ct);
    }

    internal static ChatsvcSendRequest SendBody(OutgoingMessage message, string? clientMessageId, string displayName)
    {
        if (message.Quote is not { } quote)
            return new(message.Content, message.Html ? "RichText/Html" : "Text", "text", clientMessageId, displayName);

        // 引用只能放在 HTML 裡，純文字先轉成段落
        var html = message.Html ? message.Content : TextToHtml(message.Content);
        var id = long.Parse(quote.Id, NumberStyles.None, CultureInfo.InvariantCulture);
        var refs = JsonSerializer.Serialize([new QuotedMessageRef(quote.Id, quote.SenderMri, id)], TeamsJsonContext.Default.QuotedMessageRefArray);
        return new(QuoteBlock(quote) + html, "RichText/Html", "text", clientMessageId, displayName, new ChatsvcSendProperties(refs));
    }

    /// <summary>Teams 的 Reply blockquote。伺服器會照 <c>qtdMsgs</c> 重寫作者與預覽，這裡給的只在它沒重寫時用得到。</summary>
    internal static string QuoteBlock(QuotedMessage quote) =>
        $"<blockquote itemscope=\"\" itemtype=\"http://schema.skype.com/Reply\" itemid=\"{quote.Id}\">"
        + $"<strong itemprop=\"mri\" itemid=\"{HtmlEscape(quote.SenderMri)}\">{HtmlEscape(quote.SenderName ?? "")}</strong>"
        + $"<span itemprop=\"time\" itemid=\"{quote.Id}\"></span>"
        + $"<p itemprop=\"preview\">{HtmlEscape(quote.Preview)}</p></blockquote>";

    internal static string TextToHtml(string text) => $"<p>{HtmlEscape(text).ReplaceLineEndings("<br>")}</p>";

    // 不用 WebUtility.HtmlEncode：它會把 emoji（與 U+00A0–U+00FF）轉成數字 entity
    private static string HtmlEscape(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>新訊息的 Teams id：201 的 body 是 <c>{"OriginalArrivalTime": id}</c>，<c>Location</c> 結尾也是它。都沒有就 null。</summary>
    internal static string? SentMessageId(string body, Uri? location)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (GetInt64(doc.RootElement, "OriginalArrivalTime") is > 0 and var id)
                return id.ToString(CultureInfo.InvariantCulture);
        }
        catch (JsonException)
        {
            // 沒有 body 或不是 JSON：看 Location
        }
        var last = location?.OriginalString.TrimEnd('/').Split('/')[^1];
        return last is { Length: > 0 } && last.All(char.IsAsciiDigit) ? last : null;
    }

    /// <summary>
    /// static 版的訊息解析，給 Trouter 推播事件共用。編輯看 properties.edittime、刪除看 properties.deletetime
    /// （實際資料是數字，偶爾是字串）。version 不能拿來判斷編輯：訊息有任何變動（例如有人按表情）都會往上跳。
    /// </summary>
    public static MessageDto? ParseMessage(JsonElement m, string conversationId)
    {
        var id = GetString(m, "id");
        if (id is null)
            return null;
        var version = GetInt64(m, "version");
        var props = Child(m, "properties");
        var edited = HasValue(props, "edittime");
        var deleted = HasValue(props, "deletetime");
        return new MessageDto(
            id,
            conversationId,
            Mri(GetString(m, "from")),
            GetString(m, "imdisplayname"),
            GetString(m, "messagetype"),
            GetString(m, "content"),
            GetInt64(m, "sequenceId"),
            ParseTime(GetString(m, "composetime")) ?? DateTimeOffset.UtcNow,
            version == 0 && long.TryParse(id, out var v) ? v : version,
            edited,
            deleted,
            ParseAttachments(props),
            GetString(m, "clientmessageid"));
    }

    /// <summary>從存下來的原始 JSON 取附件（DB 只存了內容欄位，附件留在 RawJson 裡）。</summary>
    public static IReadOnlyList<AttachmentDto>? ParseAttachments(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return ParseAttachments(Child(doc.RootElement, "properties"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>properties.emotions</c> → 表情代碼 →（按的人 MRI → 按的時間）。chatsvc 給的是陣列
    /// <c>[{key, users: [{mri, time}]}]</c>，也接字串化的 JSON（跟 files 一樣）。沒人按的表情不列。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, DateTimeOffset>> ParseReactions(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var emotions = Child(Child(doc.RootElement, "properties"), "emotions");
            if (emotions.ValueKind != JsonValueKind.String)
                return ParseEmotionArray(emotions);
            using var inner = JsonDocument.Parse(emotions.GetString() ?? "[]");
            return ParseEmotionArray(inner.RootElement);
        }
        catch (JsonException)
        {
            return new Dictionary<string, IReadOnlyDictionary<string, DateTimeOffset>>();
        }
    }

    private static Dictionary<string, IReadOnlyDictionary<string, DateTimeOffset>> ParseEmotionArray(JsonElement emotions)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, DateTimeOffset>>();
        if (emotions.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var emotion in emotions.EnumerateArray())
        {
            if (GetString(emotion, "key") is not { } key || Child(emotion, "users") is not { ValueKind: JsonValueKind.Array } users)
                continue;
            var byUser = new Dictionary<string, DateTimeOffset>();
            foreach (var user in users.EnumerateArray())
                if (GetString(user, "mri") is { } mri)
                    byUser[mri] = DateTimeOffset.FromUnixTimeMilliseconds(GetInt64(user, "time"));
            if (byUser.Count > 0)
                result[key] = byUser;
        }
        return result;
    }

    /// <summary><c>properties.files</c> → 附件。chatsvc 給的是「字串化的 JSON 陣列」，也接直接是陣列的情況。</summary>
    private static IReadOnlyList<AttachmentDto>? ParseAttachments(JsonElement props)
    {
        var files = Child(props, "files");
        if (files.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(files.GetString() ?? "[]");
                return ParseFileArray(doc.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return ParseFileArray(files);
    }

    private static IReadOnlyList<AttachmentDto>? ParseFileArray(JsonElement files)
    {
        if (files.ValueKind != JsonValueKind.Array)
            return null;
        var result = new List<AttachmentDto>();
        foreach (var f in files.EnumerateArray())
        {
            if ((GetString(f, "fileName") ?? GetString(f, "title")) is not { } name)
                continue;
            result.Add(new AttachmentDto(
                name,
                GetString(f, "fileType") ?? GetString(f, "type"),
                GetString(Child(f, "filePreview"), "previewUrl"),
                GetString(Child(f, "fileInfo"), "shareUrl")));
        }
        return result.Count > 0 ? result : null;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        var token = await tokens.GetAsync(ct);
        var resp = await http.SendAsync(Apply(make(), token.Token), ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            logger.LogInformation("chatsvc returned 401; refreshing the token and retrying");
            resp.Dispose();
            token = await tokens.RefreshAsync(token.Token, ct);
            resp = await http.SendAsync(Apply(make(), token.Token), ct);
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            throw new ChatsvcException(resp.StatusCode, ChatsvcException.SubCodeOf(body), $"chatsvc {(int)resp.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
        }
        return resp;
    }

    private static HttpRequestMessage Apply(HttpRequestMessage req, string token)
    {
        req.Headers.TryAddWithoutValidation("x-skypetoken", token);
        req.Headers.TryAddWithoutValidation("authentication", $"skypetoken={token}");
        req.Headers.TryAddWithoutValidation("x-ms-client-type", "cdlworker");
        req.Headers.TryAddWithoutValidation("x-ms-client-version", TeamsAuthConstants.ChatsvcClientVersion);
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

    /// <summary>有這個欄位而且不是 null（數字、字串都算）。</summary>
    private static bool HasValue(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

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

/// <summary>要發的一則訊息。<paramref name="Html"/> 為 true 時 <paramref name="Content"/> 是 HTML（送 <c>RichText/Html</c>）；<paramref name="ClientMessageId"/> 是 null 就產生亂數。</summary>
public sealed record OutgoingMessage(string Content, bool Html = false, string? ClientMessageId = null, QuotedMessage? Quote = null);

/// <summary>引用回覆的對象：Teams 訊息 id（數字）、作者 MRI 與名字、純文字預覽。</summary>
public sealed record QuotedMessage(string Id, string SenderMri, string? SenderName, string Preview);

/// <summary>
/// chatsvc 回了非 2xx。<see cref="SubCode"/> 是錯誤 body 裡的細項（例如 <c>MessageNotFound</c>、<c>MessageAlreadyDeleted</c>），
/// 藏在 <c>message</c> 欄位那段 JSON 字串裡；讀不到是 null。
/// </summary>
public sealed class ChatsvcException(HttpStatusCode status, string? subCode, string message) : InvalidOperationException(message)
{
    public HttpStatusCode Status { get; } = status;

    public string? SubCode { get; } = subCode;

    internal static string? SubCodeOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
                return null;
            using var inner = JsonDocument.Parse(message.GetString()!);
            return inner.RootElement.ValueKind == JsonValueKind.Object && inner.RootElement.TryGetProperty("subCode", out var sub)
                   && sub.ValueKind == JsonValueKind.String
                ? sub.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
