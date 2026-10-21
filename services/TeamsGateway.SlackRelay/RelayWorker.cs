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
        if (string.IsNullOrEmpty(message.Content))
            return; // 純系統事件 / 無文字內容，不轉

        var channel = routes.GetValueOrDefault(message.ConversationId) ?? defaultChannel;
        if (channel is null)
            return; // 沒對應頻道、也沒 default

        var text = await FormatAsync(message, channel, ct);
        var ts = await slack.PostMessageAsync(channel, text, ct);
        if (ts is not null)
            links.Remember(message.Id, channel, ts); // 記住對應，之後的回覆才連得回這則
    }

    private async Task<string> FormatAsync(MessageDto message, string channel, CancellationToken ct)
    {
        var who = string.IsNullOrWhiteSpace(message.FromName) ? message.From ?? "unknown" : message.FromName;
        var marker = message.Deleted ? "(deleted)" : message.Edited ? "(edited)" : null;
        var head = marker is null ? $"*{who}*:" : $"*{who}*: {marker}";

        // 若是回覆、且被引用那則我們轉過：用 Slack permalink（Slack 會 unfurl 成原生引用），不再畫 blockquote
        string? permalink = null;
        if (TeamsText.QuotedMessageId(message.Content) is { } quotedId && links.Lookup(quotedId) is { } slackRef)
            permalink = await slack.GetPermalinkAsync(slackRef.Channel, slackRef.Ts, ct);

        var body = TeamsText.ToSlack(message.Content, includeQuote: permalink is null);
        var content = permalink is null ? body : $"<{permalink}>\n{body}".TrimEnd();

        // 多行、以引用開頭、或有 permalink 時，內容另起一行
        var joiner = content.Length > 0 && (content.Contains('\n') || content.StartsWith('>') || content.StartsWith('<')) ? "\n" : " ";
        return $"{head}{joiner}{content}";
    }
}
