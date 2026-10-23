using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TeamsGateway.SlackRelay;

/// <summary>
/// 啟動時把每條路由印成「Teams 對話名稱 → Slack 頻道名稱」。設定只放 id（名稱會改、id 不會），
/// 核對哪個 id 是哪個群 / 頻道就看這幾行 log。名稱查不到只印 id，不影響轉送。
/// </summary>
public sealed class RouteLogger(
    GatewayStatusClient gateway,
    SlackClient slack,
    IOptions<RelayOptions> options,
    ILogger<RouteLogger> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var opts = options.Value;
        // 路由不完整 / 重複的警告 RelayWorker 會記，這裡不重複
        var routes = RelayWorker.BuildRoutes(opts.Slack.Routes, NullLogger.Instance);
        if (routes.Count == 0 && opts.Slack.DefaultChannel is null)
            return; // RelayWorker 會記 error、不啟動

        try
        {
            var topics = await gateway.GetChatTopicsAsync(ct);
            var channelNames = new Dictionary<string, string>();
            foreach (var channel in routes.Values.Append(opts.Slack.DefaultChannel).OfType<string>().Distinct())
                if (await slack.GetChannelNameAsync(channel, ct) is { } name)
                    channelNames[channel] = name;

            foreach (var (teams, slackChannel) in Describe(routes, opts.Slack.DefaultChannel, topics, channelNames))
                logger.LogInformation("路由：{Teams} → {Slack}", teams, slackChannel);

            // 查得到對話清單卻沒有這個 id：多半是 id 打錯，或帳號不在這個群
            if (topics is not null)
                foreach (var chat in routes.Keys.Where(c => !topics.ContainsKey(c)))
                    logger.LogWarning("路由的 Teams 對話 {Chat} 不在帳號的對話清單裡（id 打錯，或帳號不在這個群？）", chat);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 只是印 log：查名稱的例外不能冒出去，BackgroundService 掛掉會把整個 host 停掉
            logger.LogWarning(ex, "查路由的名稱失敗，略過");
        }
    }

    /// <summary>
    /// 每條路由的（Teams 標籤, Slack 標籤）：有名稱是「名稱（id）」，沒有就只有 id。
    /// 有 Routes 時只訂閱表內的對話、DefaultChannel 用不到，所以只在沒有 Routes 時列 DefaultChannel。
    /// </summary>
    internal static IEnumerable<(string Teams, string Slack)> Describe(
        IReadOnlyDictionary<string, string> routes, string? defaultChannel,
        IReadOnlyDictionary<string, string?>? topics, IReadOnlyDictionary<string, string> channelNames)
    {
        string TeamsLabel(string chat) => topics?.GetValueOrDefault(chat) is { } topic ? $"{topic}（{chat}）" : chat;
        string SlackLabel(string channel) => channelNames.TryGetValue(channel, out var name) ? $"#{name}（{channel}）" : channel;

        foreach (var (chat, channel) in routes)
            yield return (TeamsLabel(chat), SlackLabel(channel));
        if (routes.Count == 0 && defaultChannel is not null)
            yield return ("所有對話", SlackLabel(defaultChannel));
    }
}
