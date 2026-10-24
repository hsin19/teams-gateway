using TeamsGateway.SlackRelay;

// 例外不能跑出 Main：在 container 裡 dotnet 是 PID 1，例外跑出去後 runtime 會 abort()，kernel 不送 SIGABRT 給 PID 1，
// process 就卡在 100% CPU 不結束、restart policy 也不會動（ADR 0010）。自己印出來、回非 0 的 exit code。
try
{
    // 是 worker，開 HTTP 只為了 /health、/alive（ServiceDefaults 的 MapDefaultEndpoints），沒有別的端點
    var builder = WebApplication.CreateBuilder(args);

    // 部署專屬設定（Routes 等不進 repo 的對照）；gitignored，可選
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

    builder.AddServiceDefaults();
    builder.AddRelayServices();

    var app = builder.Build();
    app.MapDefaultEndpoints();
    app.Run();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Console.Error.WriteLine($"relay 因未處理的例外結束：{ex}");
    return 1;
}
