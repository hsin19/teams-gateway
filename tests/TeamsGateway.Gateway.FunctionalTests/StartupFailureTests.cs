namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>
/// 啟動失敗時 entry point 要回非 0 的 exit code，不能讓例外跑出 Main：在 container 裡 dotnet 是 PID 1，
/// 例外跑出 Main 後 runtime 會 abort()，kernel 不送 SIGABRT 給 PID 1，process 會卡住不結束（ADR 0010）。
/// </summary>
public sealed class StartupFailureTests
{
    // SQLite 檔所在的目錄不存在，跟部署時 /data 寫不進去一樣，啟動時套 migration 就失敗
    [Fact]
    public void Entry_point_returns_nonzero_when_the_database_cannot_be_opened()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tgw-startup-" + Guid.NewGuid().ToString("N"));
        string[] args =
        [
            $"--Gateway:DataDir={dir}",
            "--Gateway:Database:Provider=Sqlite",
            $"--ConnectionStrings:gateway=Data Source={Path.Combine(dir, "missing", "gateway.db")}",
        ];

        var exitCode = typeof(Program).Assembly.EntryPoint!.Invoke(null, [args]);

        Assert.Equal(1, exitCode);
    }
}
