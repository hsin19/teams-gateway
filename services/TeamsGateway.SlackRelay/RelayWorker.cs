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

        await slack.PostMessageAsync(channel, Format(message), ct);
    }

    private static string Format(MessageDto message)
    {
        var who = string.IsNullOrWhiteSpace(message.FromName) ? message.From ?? "unknown" : message.FromName;
        var tag = message.Deleted ? "(deleted) " : message.Edited ? "(edited) " : "";
        var text = TeamsText.ToSlack(message.Content); // Teams RichText/Html → Slack 看得順的文字
        var link = $"https://teams.live.com/l/message/{message.ConversationId}/{message.Id}?context=%7B%22contextType%22%3A%22chat%22%7D";
        return $"*{who}*: {tag}{text}  <{link}|🔗>";
    }
}
