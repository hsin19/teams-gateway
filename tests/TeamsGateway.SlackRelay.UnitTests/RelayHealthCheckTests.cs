using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

public class RelayHealthCheckTests
{
    // 沒 token 或沒頻道時 worker 不啟動，連不連得上都不算健康
    [Theory]
    [InlineData(null, "C0ABC")]
    [InlineData("xoxb-1", null)]
    public async Task Unhealthy_when_the_worker_would_not_start(string? botToken, string? defaultChannel)
    {
        var slack = new RelayOptions.SlackSettings { BotToken = botToken, DefaultChannel = defaultChannel };

        var result = await Check(slack, connected: true);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    // 剛啟動還沒連上、或斷線重連的空檔
    [Fact]
    public async Task Unhealthy_while_the_source_is_disconnected()
    {
        var result = await Check(Configured(), connected: false);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Healthy_when_configured_and_connected()
    {
        var result = await Check(Configured(), connected: true);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // 只有路由、沒 DefaultChannel 也算有地方轉
    // 開了發話（有 AppToken）：Socket Mode 斷了，發話就壞了，要看得出來；沒開時不看
    [Theory]
    [InlineData("xapp-1", false, HealthStatus.Unhealthy)]
    [InlineData("xapp-1", true, HealthStatus.Healthy)]
    [InlineData(null, false, HealthStatus.Healthy)]
    public async Task Socket_mode_counts_only_when_sending_is_on(string? appToken, bool socketConnected, HealthStatus expected)
    {
        var slack = Configured();
        slack.AppToken = appToken;

        var result = await Check(slack, connected: true, socketConnected);

        Assert.Equal(expected, result.Status);
    }

    private static RelayOptions.SlackSettings Configured() => new()
    {
        BotToken = "xoxb-1",
        Routes = [new() { Chat = "19:x@thread.v2", Channel = "C0ABC" }],
    };

    private static Task<HealthCheckResult> Check(RelayOptions.SlackSettings slack, bool connected, bool socketConnected = false) =>
        new RelayHealthCheck(new FakeSource(connected), new FakeSocket(socketConnected), Options.Create(new RelayOptions { Slack = slack }))
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    private sealed class FakeSource(bool connected) : IEventSource
    {
        public bool Connected => connected;

        public IAsyncEnumerable<StreamEvent> ReadAsync(IReadOnlyCollection<string>? chats, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSocket(bool connected) : ISlackSocket
    {
        public bool Connected => connected;
    }
}
