using Microsoft.Extensions.Logging.Abstractions;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

public class RouteConfigTests
{
    private static RelayOptions.ChannelRoute Route(string chat, string channel) => new() { Chat = chat, Channel = channel };

    // 設定檔手滑重複一條路由，不能讓整個 relay 起不來
    [Fact]
    public void Duplicate_chat_uses_the_last_route()
    {
        var routes = RelayWorker.BuildRoutes([Route("19:a", "C1"), Route("19:b", "C2"), Route("19:a", "C3")], NullLogger.Instance);

        Assert.Equal(new Dictionary<string, string> { ["19:a"] = "C3", ["19:b"] = "C2" }, routes);
    }

    [Fact]
    public void Incomplete_routes_are_skipped()
    {
        var routes = RelayWorker.BuildRoutes([Route("", "C1"), Route("19:a", " "), Route("19:b", "C2")], NullLogger.Instance);

        Assert.Equal(new Dictionary<string, string> { ["19:b"] = "C2" }, routes);
    }
}
