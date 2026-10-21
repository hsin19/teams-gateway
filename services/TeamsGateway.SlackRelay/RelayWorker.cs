using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// Phase 1：訂閱 gateway SSE，把 Teams 訊息轉貼進對應 Slack 頻道。
/// 斷線用 <c>Last-Event-ID</c>（= 最後收到的訊息 id）續接，指數退避重連。
/// </summary>
public sealed class RelayWorker(
    GatewayEventStream stream,
    SlackClient slack,
    MessageLinks links,
    IOptions<RelayOptions> options,
    ILogger<RelayWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.Slack.BotToken)
            || (opts.Slack.DefaultChannel is null && opts.Slack.Routes.Count == 0))
        {
            logger.LogError("未設定 Relay:Slack:BotToken 或任何頻道，relay 不啟動");
            return;
        }

        var routes = opts.Slack.Routes.ToDictionary(r => r.Chat, r => r.Channel);
        // 有路由就只訂閱那些對話；否則訂全部（走 DefaultChannel）
        var chats = routes.Count > 0 ? string.Join(',', routes.Keys) : null;
        logger.LogInformation("relay 啟動：{Count} 條路由，default={Default}", routes.Count, opts.Slack.DefaultChannel ?? "(無)");

        // 從 Slack 歷史回補近期的訊息對應（metadata 夾著 Teams id），重啟後才連得回舊訊息的回覆 / 編輯
        await WarmupLinksAsync(routes, opts.Slack.DefaultChannel, opts.Links.RetentionHours, ct);

        var backoff = MinBackoff;
        string? lastId = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var message in stream.ConnectAsync(chats, lastId, ct))
                {
                    lastId = message.Id;
                    await RelayAsync(message, routes, opts.Slack.DefaultChannel, ct);
                    backoff = MinBackoff; // 收得到就重置退避
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "gateway SSE 連線中斷，{Seconds}s 後重連", backoff.TotalSeconds);
            }

            await Task.Delay(backoff, ct);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
        }
    }

    private async Task RelayAsync(MessageDto message, Dictionary<string, string> routes, string? defaultChannel, CancellationToken ct)
    {
        var channel = routes.GetValueOrDefault(message.ConversationId) ?? defaultChannel;
        if (channel is null)
            return; // 沒對應頻道、也沒 default

        // 這則我們轉過 → 編輯 / 刪除：原地更新，舊版進 thread 留證（不加 (edited)，靠 Slack 原生標記）
        if (links.Lookup(message.Id) is { } prior)
        {
            await slack.PostReplyAsync(prior.Channel, prior.Ts, $"_上一版_\n{prior.Text}", ct);
            var updated = message.Deleted
                ? $"{Header(message)} _（訊息已刪除）_"
                : await FormatAsync(message, prior.Channel, ct);
            await slack.UpdateMessageAsync(prior.Channel, prior.Ts, updated, message.Id, ct);
            links.Remember(message.Id, prior.Channel, prior.Ts, updated);
            return;
        }

        if (string.IsNullOrEmpty(message.Content))
            return; // 新訊息但無文字內容（純系統事件），不轉

        var text = await FormatAsync(message, channel, ct);
        var ts = await slack.PostMessageAsync(channel, text, message.Id, ct);
        if (ts is not null)
            links.Remember(message.Id, channel, ts, text); // 記住對應，給之後的回覆 / 編輯用
    }

    /// <summary>加入各頻道並讀近 <paramref name="retentionHours"/> 的歷史，從 metadata 把 Teams id → Slack 訊息 撈回對應表。</summary>
    private async Task WarmupLinksAsync(Dictionary<string, string> routes, string? defaultChannel, int retentionHours, CancellationToken ct)
    {
        var channels = new HashSet<string>(routes.Values);
        if (!string.IsNullOrWhiteSpace(defaultChannel))
            channels.Add(defaultChannel);
        if (channels.Count == 0)
            return;

        var oldest = DateTimeOffset.UtcNow.AddHours(-retentionHours).ToUnixTimeSeconds();
        foreach (var channel in channels)
        {
            try
            {
                await slack.JoinChannelAsync(channel, ct); // 要是成員才讀得到歷史
                foreach (var m in await slack.ReadRelayedAsync(channel, oldest, ct))
                    links.Remember(m.TeamsId, channel, m.Ts, m.Text);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "回補 {Channel} 的訊息對應失敗（可能 app 還沒加 channels:history/join 權限）", channel);
            }
        }
        logger.LogInformation("從 Slack 回補 {Count} 筆訊息對應（近 {Hours}h）", links.Count, retentionHours);
    }

    private static string Header(MessageDto message) =>
        $"*{(string.IsNullOrWhiteSpace(message.FromName) ? message.From ?? "unknown" : message.FromName)}*:";

    private async Task<string> FormatAsync(MessageDto message, string channel, CancellationToken ct)
    {
        // 若是回覆、且被引用那則我們轉過：用 Slack permalink（Slack 會 unfurl 成原生引用），不再畫 blockquote
        string? permalink = null;
        if (TeamsText.QuotedMessageId(message.Content) is { } quotedId && links.Lookup(quotedId) is { } slackRef)
            permalink = await slack.GetPermalinkAsync(slackRef.Channel, slackRef.Ts, ct);

        var body = TeamsText.ToSlack(message.Content, includeQuote: permalink is null);
        var content = permalink is null ? body : $"<{permalink}>\n{body}".TrimEnd();

        // 多行、以引用開頭、或有 permalink 時，內容另起一行
        var joiner = content.Length > 0 && (content.Contains('\n') || content.StartsWith('>') || content.StartsWith('<')) ? "\n" : " ";
        return $"{Header(message)}{joiner}{content}";
    }
}
