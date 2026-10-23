using Microsoft.Extensions.Logging.Abstractions;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>啟動時印的路由：查得到名稱就「名稱（id）」，查不到只印 id。</summary>
public class RouteLoggerTests
{
    private const string Chat = "19:aaa@thread.v2";

    [Fact]
    public void Labels_show_names_with_ids()
    {
        var lines = RouteLogger.Describe(
            new Dictionary<string, string> { [Chat] = "C1" }, defaultChannel: null,
            topics: new Dictionary<string, string?> { [Chat] = "teams-gateway test" },
            channelNames: new Dictionary<string, string> { ["C1"] = "teams-test" });

        Assert.Equal(("teams-gateway test（19:aaa@thread.v2）", "#teams-test（C1）"), Assert.Single(lines));
    }

    // gateway 查不到（null）、1:1 沒有名稱、Slack 沒給 channels:read：都退回只印 id
    [Fact]
    public void Missing_names_fall_back_to_ids()
    {
        var routes = new Dictionary<string, string> { [Chat] = "C1", ["19:bbb@unq.gbl.spaces"] = "C2" };

        var unknown = RouteLogger.Describe(routes, null, topics: null, channelNames: new Dictionary<string, string>());
        var oneOnOne = RouteLogger.Describe(routes, null,
            topics: new Dictionary<string, string?> { ["19:bbb@unq.gbl.spaces"] = null }, channelNames: new Dictionary<string, string>());

        Assert.Equal([(Chat, "C1"), ("19:bbb@unq.gbl.spaces", "C2")], unknown);
        Assert.Equal([(Chat, "C1"), ("19:bbb@unq.gbl.spaces", "C2")], oneOnOne);
    }

    // 有 Routes 時只訂閱表內的對話，DefaultChannel 用不到：只在沒有 Routes 時列出來
    [Fact]
    public void Default_channel_is_listed_only_without_routes()
    {
        var names = new Dictionary<string, string> { ["C9"] = "teams-test" };

        var withoutRoutes = RouteLogger.Describe(new Dictionary<string, string>(), "C9", null, names);
        var withRoutes = RouteLogger.Describe(new Dictionary<string, string> { [Chat] = "C1" }, "C9", null, names);

        Assert.Equal(("所有對話", "#teams-test（C9）"), Assert.Single(withoutRoutes));
        Assert.DoesNotContain(withRoutes, line => line.Slack.Contains("C9", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Channel_name_comes_from_conversations_info()
    {
        var slack = new RecordingSlack();
        slack.Responses["conversations.info"] = """{"ok":true,"channel":{"id":"C1","name":"teams-test"}}""";

        Assert.Equal("teams-test", await Client(slack).GetChannelNameAsync("C1", TestContext.Current.CancellationToken));
    }

    // 還沒給 channels:read（或頻道不存在）：回 null，呼叫端只印 id
    [Fact]
    public async Task Channel_name_is_null_without_scope()
    {
        var slack = new RecordingSlack();
        slack.Responses["conversations.info"] = """{"ok":false,"error":"missing_scope"}""";

        Assert.Null(await Client(slack).GetChannelNameAsync("C1", TestContext.Current.CancellationToken));
    }

    private static SlackClient Client(RecordingSlack slack) =>
        new(new HttpClient(slack) { BaseAddress = new Uri("https://slack.com/api/") }, NullLogger<SlackClient>.Instance);
}
