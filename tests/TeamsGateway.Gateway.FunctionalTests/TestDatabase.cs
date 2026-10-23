using Aspire.Hosting;
using Microsoft.Extensions.Hosting;

[assembly: AssemblyFixture(typeof(TeamsGateway.Gateway.FunctionalTests.TestDatabase))]

namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>
/// 測試跑在哪個 DB：預設 SQLite（每個 fixture 一個臨時檔）；<c>GATEWAY_TEST_DB=postgres</c> 時用 Aspire 起一個 Postgres container
/// （照 eShop 的 functional test，要有 Docker 與 Aspire CLI），整個測試行程共用，每個 fixture 各開一個資料庫。
/// fixture 之間不能互相注入，所以 <see cref="GatewayAppFixture"/> 從靜態的 <see cref="PostgresConnectionString"/> 拿；
/// assembly fixture 在任何測試之前就建好。
/// </summary>
public sealed class TestDatabase : IAsyncLifetime
{
    private DistributedApplication? _app;

    public static bool UsePostgres { get; } =
        string.Equals(Environment.GetEnvironmentVariable("GATEWAY_TEST_DB"), "postgres", StringComparison.OrdinalIgnoreCase);

    /// <summary>Postgres server 的連線字串（不含資料庫名稱）；SQLite 模式是 null。</summary>
    public static string? PostgresConnectionString { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!UsePostgres)
            return;

        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(TestDatabase).Assembly.FullName,
            DisableDashboard = true,
        });
        var postgres = builder.AddPostgres("postgres");
        _app = builder.Build();
        await _app.StartAsync();
        // container 起來不代表收得了連線：等 Aspire 的健康檢查過了才開始測
        await _app.ResourceNotifications.WaitForResourceHealthyAsync(postgres.Resource.Name);
        PostgresConnectionString = await postgres.Resource.GetConnectionStringAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
            return;
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
