using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>
/// 啟動失敗時 entry point 要回非 0 的 exit code，不能讓例外跑出 Main：在 container 裡 dotnet 是 PID 1，
/// 例外跑出 Main 後 runtime 會 abort()，kernel 不送 SIGABRT 給 PID 1，process 會卡住不結束（ADR 0010）。
/// </summary>
public class StartupFailureTests
{
    // 設定錯到 relay 起不來（不支援的 Relay:Source 在註冊服務時就丟例外）
    [Fact]
    public void Entry_point_returns_nonzero_when_startup_fails()
    {
        string[] args = ["--Relay:Source=Nope"];

        var exitCode = typeof(RelayOptions).Assembly.EntryPoint!.Invoke(null, [args]);

        Assert.Equal(1, exitCode);
    }
}
