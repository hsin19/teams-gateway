using TeamsGateway.Gateway;

// 例外不能跑出 Main：在 container 裡 dotnet 是 PID 1，例外跑出去後 runtime 會 abort()，kernel 不送 SIGABRT 給 PID 1，
// process 就卡在 100% CPU 不結束、restart policy 也不會動（ADR 0010）。自己印出來、回非 0 的 exit code。
// HostAbortedException 是 WebApplicationFactory 拿到 host 後用來中止 Main 的，要放它過去。
try
{
    var builder = WebApplication.CreateBuilder(args);

    // Aspire ServiceDefaults：OpenTelemetry、health check、HTTP resilience、service discovery
    builder.AddServiceDefaults();
    builder.AddApplicationServices();

    var app = builder.Build();

    await app.UseApplicationAsync();

    app.Run();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Console.Error.WriteLine($"gateway 因未處理的例外結束：{ex}");
    return 1;
}

// 讓 functional test 的 WebApplicationFactory<Program> 參照得到
public partial class Program;
