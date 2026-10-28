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

    public async Task<SendMessageResult> SendMessageAsync(string threadId, string text, string displayName, CancellationToken ct = default)
    {
        // 亂數而不是時間戳：同一毫秒的兩個發話不能撞號（Teams 用它認同一則訊息）
        var clientMessageId = Random.Shared.NextInt64(1_000_000_000_000_000_000, long.MaxValue).ToString(CultureInfo.InvariantCulture);
        var body = SendBody(text, clientMessageId, displayName);
        using var resp = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"conversations/{Uri.EscapeDataString(threadId)}/messages")
            {
                Content = JsonContent.Create(body, TeamsJsonContext.Default.ChatsvcSendRequest),
            },
            ct);
        return new SendMessageResult(clientMessageId);
    }

    internal static ChatsvcSendRequest SendBody(string text, string clientMessageId, string displayName) =>
        new(text, MessageType: "Text", ContentType: "text", clientMessageId, displayName);

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
            ParseAttachments(props));
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
            throw new InvalidOperationException($"chatsvc {(int)resp.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
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
