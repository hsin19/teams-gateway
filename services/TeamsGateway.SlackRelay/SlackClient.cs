using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// Slack Web API：發訊息 / 原地更新 / thread 回覆 / 取 permalink / 加入頻道 / 讀歷史 / 上傳圖片。
/// 轉過去的訊息會夾一個 metadata（Teams 訊息 id），讓重啟時能從 Slack 歷史重建對應。
/// 有圖的訊息改用 blocks：文字 section + 每張圖一個 image block（引用先上傳好的 Slack 檔案）。
/// </summary>
public sealed class SlackClient(HttpClient http, ILogger<SlackClient> logger)
{
    private const string MetaEventType = "teams_relay";
    private const int SectionMaxChars = 3000;

    // 剛上傳的檔 Slack 可能還沒處理完，這時引用它的 image block 會回 invalid_blocks
    private static readonly TimeSpan[] FileNotReadyDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    // version：目前顯示的是 Teams 哪一版（毫秒時間戳，原文 = 訊息 id、編輯後 = 編輯時間），重啟回補後留證才標得出時間
    private static SlackMetadata? Meta(string? teamsId, long? version) =>
        teamsId is null ? null : new SlackMetadata(MetaEventType, new SlackRelayPayload(teamsId, version));

    /// <summary>發訊息（<paramref name="teamsId"/> / <paramref name="version"/> 夾進 metadata、<paramref name="images"/> 是已上傳的 Slack file id）；回傳貼成的 ts，失敗 null。</summary>
    public async Task<string?> PostMessageAsync(string channel, string text, string? teamsId, long? version, IReadOnlyList<string> images, CancellationToken ct)
    {
        var result = await SendAsync("chat.postMessage", channel, text, images, clearBlocks: false,
            (t, blocks) => new SlackMessageRequest(channel, t, blocks, Metadata: Meta(teamsId, version)), ct);
        return result?.Ts;
    }

    /// <summary>原地更新一則訊息（編輯用；Slack 自帶 "edited"）。metadata 要一起帶，不然更新後會掉。</summary>
    public async Task UpdateMessageAsync(string channel, string ts, string text, string teamsId, long? version, IReadOnlyList<string> images, CancellationToken ct) =>
        await SendAsync("chat.update", channel, text, images, clearBlocks: true,
            (t, blocks) => new SlackMessageRequest(channel, t, blocks, Ts: ts, Metadata: Meta(teamsId, version)), ct);

    /// <summary>在某則訊息的 thread 底下回一則（舊版留證用）。</summary>
    public async Task PostReplyAsync(string channel, string threadTs, string text, IReadOnlyList<string> images, CancellationToken ct) =>
        await SendAsync("chat.postMessage", channel, text, images, clearBlocks: false,
            (t, blocks) => new SlackMessageRequest(channel, t, blocks, ThreadTs: threadTs), ct);

    /// <summary>
    /// 上傳一張圖但不分享到任何頻道（留 private），回傳 file id 給 image block 的 <c>slack_file</c> 引用；失敗 null。
    /// 三步：<c>files.getUploadURLExternal</c> → POST 檔案內容 → <c>files.completeUploadExternal</c>。需要 <c>files:write</c>。
    /// </summary>
    public async Task<string?> UploadFileAsync(byte[] bytes, string filename, CancellationToken ct)
    {
        using var ticketResp = await http.PostAsync("files.getUploadURLExternal", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["filename"] = filename,
            ["length"] = bytes.Length.ToString(CultureInfo.InvariantCulture),
        }), ct);
        var ticket = await ticketResp.Content.ReadFromJsonAsync(SlackJsonContext.Default.UploadTicketResponse, ct);
        if (ticket is not { Ok: true, UploadUrl: { } uploadUrl, FileId: { } fileId })
        {
            logger.LogWarning("Slack files.getUploadURLExternal 失敗：{Error}{Hint}",
                ticket?.Error ?? ((int)ticketResp.StatusCode).ToString(CultureInfo.InvariantCulture),
                ticket?.Error == "missing_scope" ? "（Slack app 要加 files:write 並重新安裝）" : "");
            return null;
        }

        using (var uploadResp = await http.PostAsync(uploadUrl, new ByteArrayContent(bytes), ct))
        {
            if (!uploadResp.IsSuccessStatusCode)
            {
                logger.LogWarning("上傳圖片內容到 Slack 失敗 HTTP {Status}", (int)uploadResp.StatusCode);
                return null;
            }
        }

        var files = JsonSerializer.Serialize([new UploadedFile(fileId, filename)], SlackJsonContext.Default.UploadedFileArray);
        using var doneResp = await http.PostAsync("files.completeUploadExternal",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["files"] = files }), ct);
        var done = await doneResp.Content.ReadFromJsonAsync(SlackJsonContext.Default.PostResponse, ct);
        if (done is { Ok: true })
            return fileId;
        logger.LogWarning("Slack files.completeUploadExternal 失敗：{Error}", done?.Error ?? ((int)doneResp.StatusCode).ToString(CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>某則訊息的 permalink（貼進訊息裡 Slack 會 unfurl 成原生引用）；取不到回 null。</summary>
    public async Task<string?> GetPermalinkAsync(string channel, string ts, CancellationToken ct)
    {
        var url = $"chat.getPermalink?channel={Uri.EscapeDataString(channel)}&message_ts={Uri.EscapeDataString(ts)}";
        using var resp = await http.GetAsync(url, ct);
        var result = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.PermalinkResponse, ct);
        return result is { Ok: true } ? result.Permalink : null;
    }

    /// <summary>bot 加入頻道（才讀得到歷史；public 頻道用 <c>channels:join</c>）。已在裡面也回 ok。</summary>
    public async Task JoinChannelAsync(string channel, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync("conversations.join", new JoinRequest(channel), SlackJsonContext.Default.JoinRequest, ct);
        var result = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.PostResponse, ct);
        if (result is not { Ok: true })
            logger.LogWarning("Slack conversations.join 失敗（channel={Channel}）：{Error}", channel, result?.Error ?? ((int)resp.StatusCode).ToString());
    }

    /// <summary>讀頻道歷史，撈出我們轉過（帶 metadata）的訊息：(Teams id, ts, 當前文字, 版本)。往回翻到 <paramref name="oldestUnixSeconds"/>。</summary>
    public async Task<IReadOnlyList<RelayedMessage>> ReadRelayedAsync(string channel, long oldestUnixSeconds, CancellationToken ct)
    {
        var found = new List<RelayedMessage>();
        string? cursor = null;
        for (var page = 0; page < 20; page++) // 上限保護
        {
            var url = $"conversations.history?channel={Uri.EscapeDataString(channel)}&oldest={oldestUnixSeconds}&limit=200&include_all_metadata=true"
                      + (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var resp = await http.GetAsync(url, ct);
            var body = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.HistoryResponse, ct);
            if (body is not { Ok: true })
            {
                logger.LogWarning("Slack conversations.history 失敗（channel={Channel}）：{Error}", channel, body?.Error ?? ((int)resp.StatusCode).ToString());
                break;
            }
            foreach (var m in body.Messages ?? [])
                if (m.Metadata?.EventType == MetaEventType && m.Metadata.EventPayload?.TeamsId is { } tid && m.Ts is { } ts)
                    found.Add(new RelayedMessage(tid, ts, m.Text ?? "", m.Metadata.EventPayload.Version));
            cursor = body.ResponseMetadata?.NextCursor;
            if (string.IsNullOrEmpty(cursor))
                break;
        }
        return found;
    }

    /// <summary>文字切成 mrkdwn section（每塊上限 3000 字）+ 每張圖一個引用 Slack 檔案的 image block。</summary>
    internal static IReadOnlyList<SlackBlock> ImageBlocks(string text, IReadOnlyList<string> fileIds)
    {
        var blocks = new List<SlackBlock>();
        for (var i = 0; i < text.Length; i += SectionMaxChars)
            blocks.Add(SlackBlock.Section(text.Substring(i, Math.Min(SectionMaxChars, text.Length - i))));
        blocks.AddRange(fileIds.Select(SlackBlock.Image));
        return blocks;
    }

    /// <summary>
    /// 送一則訊息：有圖就帶 blocks（文字 section + image block），沒圖只帶 text。
    /// 圖剛上傳完 Slack 可能回 invalid_blocks，等一下重試；一直不行就拿掉圖、文字補註記改送純文字，至少內容送得到。
    /// <paramref name="clearBlocks"/>：chat.update 沒帶 blocks 時 Slack 會保留舊的（舊圖留著），所以沒圖要明確送空陣列。
    /// </summary>
    private async Task<PostResponse?> SendAsync(
        string method, string channel, string text, IReadOnlyList<string> images, bool clearBlocks,
        Func<string, IReadOnlyList<SlackBlock>?, SlackMessageRequest> body, CancellationToken ct)
    {
        var withImages = images.Count > 0;
        for (var attempt = 0; ; attempt++)
        {
            IReadOnlyList<SlackBlock>? blocks = withImages ? ImageBlocks(text, images) : clearBlocks ? [] : null;
            using var resp = await http.PostAsJsonAsync(method, body(text, blocks), SlackJsonContext.Default.SlackMessageRequest, ct);
            var result = await resp.Content.ReadFromJsonAsync(SlackJsonContext.Default.PostResponse, ct);
            if (withImages && result is { Ok: false, Error: "invalid_blocks" })
            {
                if (attempt < FileNotReadyDelays.Length)
                {
                    await Task.Delay(FileNotReadyDelays[attempt], ct);
                    continue;
                }
                logger.LogWarning("Slack {Method} 帶圖一直 invalid_blocks（channel={Channel}），改送純文字", method, channel);
                withImages = false;
                text += $"\n_({images.Count} image{(images.Count == 1 ? "" : "s")} could not be displayed)_";
                continue;
            }
            if (result is { Ok: true })
                return result;
            logger.LogError("Slack {Method} 失敗（channel={Channel}）：{Error}", method, channel, result?.Error ?? ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
            return null;
        }
    }

    public readonly record struct RelayedMessage(string TeamsId, string Ts, string Text, long? Version);
}
