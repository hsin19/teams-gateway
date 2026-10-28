using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.Mcp;

/// <summary>
/// 把 gateway 的能力開成 MCP tool。讀 / 發就是 function，沒有需要 LLM 介入的流程，所以用 MCP 而不是 skill。
/// 輸出是給 LLM 讀的 log 式純文字（<see cref="McpText"/>）。`chat` 參數接 thread id（19:...）或群名，gateway 端自己解析。
/// SDK 只把 <see cref="McpException"/> 的訊息傳給 client，其他例外只剩一句通用錯誤，所以找不到群、群名對到多個的候選、
/// chatsvc 的錯誤都要轉成它，agent 才看得到、才能自己修正。
/// </summary>
[McpServerToolType]
public sealed class TeamsTools // 不能是 static：WithTools<T> 要拿它當型別參數
{
    private const int DefaultLimit = 50;
    private const int DefaultChatLimit = 30;
    // 一則（一個對話）一行大約幾十 token；壓在 client 對單次 tool 輸出的限制（Claude Code 預設 25k token）以下
    private const int MaxLimit = 200;
    private const int MaxImages = 10;
    // Claude 單張圖片的上限
    private const long MaxImageBytes = 5 * 1024 * 1024;

    private const string ChatHelp = "Thread id (19:…) or group name";
    private const string TimezoneHelp =
        "Timezone for the times shown, and for since/until given without an offset, as a UTC offset like +08:00. Default: the server setting";

    [McpServerTool(Name = "list_chats"), Description(
        "List Teams chats (groups and 1:1), most recent activity first, one per line: `<thread id> | <topic> | last <time>`. Each response lists at most `limit` chats; if more were left out, pass the offset from the last line (`# more: offset=...`) to continue. Pass the thread id as `chat` to the other tools.")]
    public static async Task<string> ListChats(
        ChatsvcClient chatsvc,
        IOptions<GatewayOptions> options,
        [Description("Keyword to filter topics by, case-insensitive (optional)")] string? query = null,
        [Description("Max chats, 1-200 (default 30); the most recently active ones are kept")] int limit = DefaultChatLimit,
        [Description("Number of chats to skip; pass the offset from `# more:` to continue (default 0)")] int offset = 0,
        [Description(TimezoneHelp)] string? timezone = null,
        CancellationToken ct = default)
    {
        var zone = McpText.ParseZone(timezone ?? options.Value.McpTimeZone);
        var chats = await CallTeams(() => chatsvc.GetConversationsAsync(ct));
        if (!string.IsNullOrWhiteSpace(query))
            chats = chats.Where(c => c.Topic?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true).ToList();
        return McpText.Chats(chats, zone, Math.Clamp(limit, 1, MaxLimit), Math.Max(0, offset));
    }

    [McpServerTool(Name = "read_messages"), Description("""
        Read a Teams chat as a log, oldest first, one message per line: `<time> <sender>[ (edited)]: <text>`. Continuation lines of a multi-line message are indented.
        Markers: `[re <author>: "<quote>"]` reply quote, `@name` mention, `[image #<id>]` / `[N images #<id>]` and `[file: <name> #<id>]` (open them with get_attachments), `(deleted)`.
        Returns the newest `limit` messages sent in [since, until). If older messages in that range were left out, the last line is `# more: until=<time>`; call again with that `until` to keep going back.
        """)]
    public static async Task<string> ReadMessages(
        ChatsvcClient chatsvc,
        IPeopleDirectory people,
        IOptions<GatewayOptions> options,
        [Description(ChatHelp)] string chat,
        [Description("Only messages sent at or after this time: ISO 8601 like 2026-10-27T09:00:00+08:00, or 2026-10-27 09:00 in `timezone` (optional)")] string? since = null,
        [Description("Only messages sent before this time, same formats as `since`; use the value from `# more:` to page back (optional)")] string? until = null,
        [Description("Max messages, 1-200 (default 50); the newest ones in the range are kept")] int limit = DefaultLimit,
        [Description(TimezoneHelp)] string? timezone = null,
        CancellationToken ct = default)
    {
        var zone = McpText.ParseZone(timezone ?? options.Value.McpTimeZone);
        var from = McpText.ParseTime(since, zone, nameof(since));
        var to = McpText.ParseTime(until, zone, nameof(until));
        var id = await CallTeams(() => chatsvc.ResolveChatAsync(chat, ct));
        var window = await CallTeams(() => chatsvc.GetWindowAsync(id, from, to, Math.Clamp(limit, 1, MaxLimit), ct));
        var mris = window.Messages.SelectMany(McpText.QuotedAuthorMris).Distinct().ToList();
        var names = mris.Count == 0 ? new Dictionary<string, string>() : await people.DisplayNamesAsync(mris, ct);
        return McpText.Transcript(id, window, zone, mri => names.GetValueOrDefault(mri));
    }

    [McpServerTool(Name = "get_attachments"), Description(
        "Open the images and files of one Teams message, by the message id from an `[image #<id>]` or `[file: <name> #<id>]` marker in read_messages. Images (inline or attached) come back as image content; downloading other files isn't implemented yet.")]
    public static async Task<CallToolResult> GetAttachments(
        ChatsvcClient chatsvc,
        MediaClient media,
        [Description(ChatHelp)] string chat,
        [Description("Message id from the marker")] string message,
        CancellationToken ct = default)
    {
        message = message.Trim().TrimStart('#');
        var id = await CallTeams(() => chatsvc.ResolveChatAsync(chat, ct));
        var found = await CallTeams(() => chatsvc.FindMessageAsync(id, message, ct))
            ?? throw new McpException($"No message {message} in {id}");
        return await AttachmentsAsync(found, (url, n) => FetchImageAsync(media, url, n, ct));
    }

    /// <summary>圖片照順序抓（最多 <see cref="MaxImages"/> 張），檔案只列名字；只有檔案、沒有圖片時整個回錯誤，agent 才知道拿不到。</summary>
    internal static async Task<CallToolResult> AttachmentsAsync(MessageDto message, Func<string, int, Task<ContentBlock>> fetchImage)
    {
        var images = McpText.ImageUrls(message);
        var files = McpText.Files(message).Select(f => f.Name).ToList();
        if (images.Count == 0)
            throw new McpException(files.Count == 0
                ? $"Message {message.Id} has no images or files"
                : $"Downloading files isn't implemented yet ({string.Join(", ", files)}); ask the user to open them in Teams");

        var summary = $"{images.Count} image(s) in message {message.Id}" + (images.Count > MaxImages ? $", showing the first {MaxImages}" : "");
        if (files.Count > 0)
            summary += $"; files not downloadable yet: {string.Join(", ", files)}";
        var blocks = new List<ContentBlock> { new TextContentBlock { Text = summary } };
        var n = 0;
        foreach (var url in images.Take(MaxImages))
            blocks.Add(await fetchImage(url, ++n));
        return new CallToolResult { Content = blocks };
    }

    [McpServerTool(Name = "send_message"), Description(
        "Send a message to a Teams chat as the gateway's account. It can't be taken back. A group name that matches several chats fails and lists the candidates instead of guessing.")]
    public static async Task<SendMessageResult> SendMessage(
        ChatsvcClient chatsvc,
        IOptions<GatewayOptions> options,
        [Description(ChatHelp)] string chat,
        [Description("Message text")] string text,
        CancellationToken ct = default)
    {
        var id = await CallTeams(() => chatsvc.ResolveChatAsync(chat, ct));
        return await CallTeams(() => chatsvc.SendMessageAsync(id, text, options.Value.DisplayName, ct));
    }

    private static async Task<T> CallTeams<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    /// <summary>一張抓不到、太大、不是圖片時，換成一行說明，其他張照樣回。</summary>
    private static async Task<ContentBlock> FetchImageAsync(MediaClient media, string url, int n, CancellationToken ct)
    {
        if (!MediaClient.IsAllowed(url, out var uri))
            return new TextContentBlock { Text = $"image {n}: skipped, not a Teams media URL" };
        try
        {
            var (bytes, contentType) = await media.GetAsync(uri, ct);
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return new TextContentBlock { Text = $"image {n}: skipped, not an image ({contentType})" };
            if (bytes.Length > MaxImageBytes)
                return new TextContentBlock { Text = $"image {n}: skipped, too large ({bytes.Length / 1024 / 1024} MB)" };
            return ImageContentBlock.FromBytes(bytes, contentType);
        }
        catch (InvalidOperationException ex)
        {
            return new TextContentBlock { Text = $"image {n}: {ex.Message}" };
        }
    }
}
