using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

public class AuthAlertGateTests
{
    // 門檻 2：連續兩次 failed 才放行
    private static AuthAlertGate Gate() => new(failuresBeforeAlert: 2);

    [Fact]
    public void Healthy_never_alerts()
    {
        var gate = Gate();
        Assert.False(gate.Observe(true));
        Assert.False(gate.Observe(true));
    }

    [Fact]
    public void Alerts_once_on_ok_to_failed_transition()
    {
        var gate = Gate();
        Assert.False(gate.Observe(true));   // 正常
        Assert.False(gate.Observe(false));  // 第 1 次失效，未達門檻
        Assert.True(gate.Observe(false));   // 第 2 次失效 → 發
        Assert.False(gate.Observe(false));  // 持續失效，不再發
        Assert.False(gate.Observe(false));
    }

    [Fact]
    public void Never_ok_is_bootstrap_not_an_outage()
    {
        var gate = Gate();
        Assert.False(gate.Observe(false));
        Assert.False(gate.Observe(false));
        Assert.False(gate.Observe(false));
    }

    [Fact]
    public void Transient_blip_below_threshold_does_not_alert()
    {
        var gate = Gate();
        gate.Observe(true);
        Assert.False(gate.Observe(false)); // 只掉一次
        Assert.False(gate.Observe(true));  // 又好了
    }

    [Fact]
    public void Second_outage_after_recovery_alerts_again()
    {
        var gate = Gate();
        gate.Observe(true);
        gate.Observe(false);
        Assert.True(gate.Observe(false));  // 第一次中斷
        gate.Observe(true);                // 恢復
        gate.Observe(false);
        Assert.True(gate.Observe(false));  // 第二次中斷，會再發
    }

    [Fact]
    public void Unknown_does_not_change_state_or_alert()
    {
        var gate = Gate();
        gate.Observe(true);
        Assert.False(gate.Observe(null));   // 未知
        Assert.False(gate.Observe(false));  // 第 1 次失效
        Assert.False(gate.Observe(null));   // 未知不累計
        Assert.True(gate.Observe(false));   // 第 2 次失效 → 發
    }
}
