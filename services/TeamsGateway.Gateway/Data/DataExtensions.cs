using Microsoft.EntityFrameworkCore;

namespace TeamsGateway.Gateway.Data;

public static class DataExtensions
{
    /// <summary>
    /// 註冊 gateway 的 DB。連線字串優先吃 <c>ConnectionStrings:gateway</c>（之後 Aspire 接正式 DB 時由它注入），
    /// 沒有就用 <c>Gateway:DataDir</c>（預設 content root 下的 <c>.data</c>）裡的 SQLite 檔。
    /// 換正式 DB 時只要改這裡的 provider 與連線字串。
    /// </summary>
    public static IHostApplicationBuilder AddGatewayDatabase(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("gateway");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var dataDir = builder.Configuration["Gateway:DataDir"]
                ?? Path.Combine(builder.Environment.ContentRootPath, ".data");
            Directory.CreateDirectory(dataDir);
            connectionString = $"Data Source={Path.Combine(dataDir, "gateway.db")}";
        }

        // 背景工作與 HTTP 端點都用 factory，拿到的是短命 context；低流量不需要 pooling。
        builder.Services.AddDbContextFactory<GatewayDbContext>(options => options.UseSqlite(connectionString));

        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("db");
        return builder;
    }

    /// <summary>單一實例服務，啟動時直接套用 migration。</summary>
    public static async Task MigrateGatewayDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GatewayDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
    }
}
