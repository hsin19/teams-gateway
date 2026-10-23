using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace TeamsGateway.Gateway.Data;

public static class DataExtensions
{
    /// <summary>
    /// 註冊 gateway 的 DB（ADR 0008），<c>Gateway:Database:Provider</c> 選 provider：
    /// <c>Sqlite</c>（預設）吃 <c>ConnectionStrings:gateway</c>，沒有就用 <c>Gateway:DataDir</c>（預設 content root 下的 <c>.data</c>）裡的檔；
    /// <c>Postgres</c> 一定要給 <c>ConnectionStrings:gateway</c>，而且要一個 gateway 專用的資料庫（見 <see cref="MigrateAsync"/>）。
    /// </summary>
    public static IHostApplicationBuilder AddGatewayDatabase(this IHostApplicationBuilder builder)
    {
        var provider = builder.Configuration.GetValue("Gateway:Database:Provider", DatabaseProvider.Sqlite);
        var connectionString = builder.Configuration.GetConnectionString("gateway");

        if (provider == DatabaseProvider.Postgres)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Gateway:Database:Provider 是 Postgres，但沒有設 ConnectionStrings:gateway");
            AddFactory<PostgresGatewayDbContext>(builder.Services, options => options.UseNpgsql(connectionString));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                var dataDir = builder.Configuration["Gateway:DataDir"]
                    ?? Path.Combine(builder.Environment.ContentRootPath, ".data");
                Directory.CreateDirectory(dataDir);
                connectionString = $"Data Source={Path.Combine(dataDir, "gateway.db")}";
            }
            AddFactory<SqliteGatewayDbContext>(builder.Services, options => options.UseSqlite(connectionString));
        }

        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("db");
        return builder;
    }

    // 背景工作與 HTTP 端點都用 factory，拿到的是短命 context；低流量不需要 pooling。
    // 註冊的是 provider 的子類別（各有一套 migration），對外照樣是 IDbContextFactory<GatewayDbContext>
    private static void AddFactory<TContext>(IServiceCollection services, Action<DbContextOptionsBuilder> configure)
        where TContext : GatewayDbContext
    {
        services.AddDbContextFactory<TContext>(configure);
        services.AddSingleton<IDbContextFactory<GatewayDbContext>>(sp =>
            new BaseContextFactory<TContext>(sp.GetRequiredService<IDbContextFactory<TContext>>()));
    }

    /// <summary>啟動時把 DB 升到最新的 migration（見 <see cref="MigrateAsync"/>）。</summary>
    public static Task MigrateGatewayDatabaseAsync(this IServiceProvider services, CancellationToken ct = default) =>
        MigrateAsync(services.GetRequiredService<IDbContextFactory<GatewayDbContext>>(), ct);

    /// <summary>
    /// 套用還沒跑過的 migration（ADR 0009）：新的 DB 照全部 migration 建，舊的原地升級，登入 cookie、API key 都留著。
    /// 有表卻沒有 migration 紀錄的 DB 直接套會撞「表已經存在」，停下來說怎麼接：導入 migration 之前用 EnsureCreated 建的，
    /// 或是 Postgres 跟別的服務共用了同一個資料庫（要給 gateway 專用的）。
    /// </summary>
    internal static async Task MigrateAsync(IDbContextFactory<GatewayDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (await creator.ExistsAsync(ct) && await creator.HasTablesAsync(ct) && !(await db.Database.GetAppliedMigrationsAsync(ct)).Any())
            throw new InvalidOperationException(
                $"資料庫（{db.Database.GetDbConnection().DataSource}）有表卻沒有 migration 紀錄：" +
                "是導入 migration 之前用 EnsureCreated 建的，或是 Postgres 跟別的服務共用了同一個資料庫。" +
                "schema 跟基線一樣的話補一筆基線紀錄就能接上（見 docs/adr/0009）；不然就重建 DB、重新 bootstrap。");
        await db.Database.MigrateAsync(ct);
    }
}

/// <summary>把 provider 子類別的 factory 當成 <c>IDbContextFactory&lt;GatewayDbContext&gt;</c> 用。</summary>
internal sealed class BaseContextFactory<TContext>(IDbContextFactory<TContext> inner) : IDbContextFactory<GatewayDbContext>
    where TContext : GatewayDbContext
{
    public GatewayDbContext CreateDbContext() => inner.CreateDbContext();

    public async Task<GatewayDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        await inner.CreateDbContextAsync(cancellationToken);
}
