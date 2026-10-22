using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>
/// 把整個 gateway 跑在測試宿主裡、打真 HTTP，但資料落臨時目錄、關掉會連 Teams 的背景服務。
/// 測的是「我們的接線」（auth / 路由 / EF / 序列化）；會連 chatsvc 的讀發交給 E2E canary。
/// </summary>
public sealed class GatewayAppFixture : WebApplicationFactory<Program>
{
    public string AdminToken { get; } = "func-admin-" + Guid.NewGuid().ToString("N");

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "tgw-func-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 資料（SQLite + DataProtection key）落臨時目錄，不碰真的 .data
        builder.UseSetting("Gateway:DataDir", _dataDir);
        builder.UseSetting("Gateway:AdminToken", AdminToken);

        builder.ConfigureTestServices(services =>
        {
            // 這三個會在啟動時連 Teams（Trouter 連線、歷史回補、慢更新）——functional test 不要
            RemoveHostedService<TrouterBackgroundService>(services);
            RemoveHostedService<StartupBackfillService>(services);
            RemoveHostedService<ReconcileService>(services);
            RemoveHostedService<PollService>(services);
        });
    }

    private static void RemoveHostedService<T>(IServiceCollection services) where T : IHostedService
    {
        var descriptor = services.FirstOrDefault(s =>
            s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(T));
        if (descriptor is not null)
            services.Remove(descriptor);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_dataDir))
            try { Directory.Delete(_dataDir, recursive: true); } catch { /* 臨時目錄，清不掉就算了 */ }
    }
}
