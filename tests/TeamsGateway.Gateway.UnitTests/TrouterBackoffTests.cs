using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

public class TrouterBackoffTests
{
    private static readonly TimeSpan Instant = TimeSpan.FromMilliseconds(200);

    [Fact]
    public void Connection_closed_right_away_keeps_backing_off()
    {
        // 一連上就被正常關閉：不能每次都從 0 秒重來
        var delay = TimeSpan.Zero;
        var delays = new List<TimeSpan>();
        for (var i = 0; i < 7; i++)
            delays.Add(delay = TrouterBackgroundService.DelayAfter(delay, Instant));

        Assert.Equal([5, 10, 20, 40, 80, 120, 120], delays.Select(d => d.TotalSeconds));
    }

    [Fact]
    public void Stable_connection_resets_backoff() =>
        Assert.Equal(TimeSpan.FromSeconds(5), TrouterBackgroundService.DelayAfter(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30)));
}
