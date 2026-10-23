using System.Security.Cryptography;
using System.Text;
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

    /// <summary>啟動時確認 DB 跟目前的 model 一致（見 <see cref="EnsureSchemaAsync"/>）。</summary>
    public static Task EnsureGatewayDatabaseAsync(this IServiceProvider services, CancellationToken ct = default) =>
        EnsureSchemaAsync(services.GetRequiredService<IDbContextFactory<GatewayDbContext>>(), ct);

    /// <summary>
    /// 正式部署前不做 migration：DB 不存在就照目前的 model 建，並把 schema 雜湊記在 <c>PRAGMA user_version</c>。
    /// 已存在但雜湊對不上（model 改過、或是舊版 migration 建的）→ 直接停下來說要刪檔重建，
    /// 不要等跑到一半才噴 <c>no such column</c>。
    /// </summary>
    internal static async Task EnsureSchemaAsync(IDbContextFactory<GatewayDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var expected = SchemaVersion(db);
        await db.Database.OpenConnectionAsync(ct);
        if (await db.Database.EnsureCreatedAsync(ct))
        {
            await ExecuteAsync(db, $"PRAGMA user_version = {expected}", ct);
            return;
        }
        if (await ExecuteAsync(db, "PRAGMA user_version", ct) is long actual && actual != expected)
            throw new InvalidOperationException(
                $"資料庫 schema 跟程式的 model 不一致（{db.Database.GetDbConnection().DataSource}）。" +
                "正式部署前不做 migration：刪掉這個 SQLite 檔再啟動就會照新 model 重建；" +
                "裡面的登入 cookie、API key 會一起清掉，要重新 bootstrap（匯入 cookie、建 key）。");
    }

    /// <summary>model 產生的建表 SQL 的雜湊（正數 int，放得進 user_version）。</summary>
    private static int SchemaVersion(GatewayDbContext db) =>
        BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(db.Database.GenerateCreateScript()))) & int.MaxValue;

    private static async Task<object?> ExecuteAsync(GatewayDbContext db, string sql, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct);
    }
}
