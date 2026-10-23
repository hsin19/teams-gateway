using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace TeamsGateway.Gateway.Data;

public static class DataExtensions
{
    /// <summary>
    /// 註冊 gateway 的 DB（ADR 0008），<c>Gateway:Database:Provider</c> 選 provider：
    /// <c>Sqlite</c>（預設）吃 <c>ConnectionStrings:gateway</c>，沒有就用 <c>Gateway:DataDir</c>（預設 content root 下的 <c>.data</c>）裡的檔；
    /// <c>Postgres</c> 一定要給 <c>ConnectionStrings:gateway</c>，而且要一個 gateway 專用的資料庫（見 <see cref="EnsureSchemaAsync"/>）。
    /// </summary>
    public static IHostApplicationBuilder AddGatewayDatabase(this IHostApplicationBuilder builder)
    {
        var provider = builder.Configuration.GetValue("Gateway:Database:Provider", DatabaseProvider.Sqlite);
        var connectionString = builder.Configuration.GetConnectionString("gateway");

        // 背景工作與 HTTP 端點都用 factory，拿到的是短命 context；低流量不需要 pooling。
        if (provider == DatabaseProvider.Postgres)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Gateway:Database:Provider 是 Postgres，但沒有設 ConnectionStrings:gateway");
            builder.Services.AddDbContextFactory<GatewayDbContext>(options => options.UseNpgsql(connectionString));
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
            builder.Services.AddDbContextFactory<GatewayDbContext>(options => options.UseSqlite(connectionString));
        }

        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("db");
        return builder;
    }

    /// <summary>啟動時確認 DB 跟目前的 model 一致（見 <see cref="EnsureSchemaAsync"/>）。</summary>
    public static Task EnsureGatewayDatabaseAsync(this IServiceProvider services, CancellationToken ct = default) =>
        EnsureSchemaAsync(services.GetRequiredService<IDbContextFactory<GatewayDbContext>>(), ct);

    /// <summary>
    /// 正式部署前不做 migration：DB 沒有表就照目前的 model 建，並記下 schema 雜湊。
    /// 已經有表但雜湊對不上（model 改過、或是舊版 migration 建的）→ 直接停下來說要重建，
    /// 不要等跑到一半才噴 <c>no such column</c>。Postgres 有別的表也算「已經有表」，所以要給 gateway 專用的資料庫。
    /// </summary>
    internal static async Task EnsureSchemaAsync(IDbContextFactory<GatewayDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var expected = SchemaVersion(db);
        // 先建再連：Postgres 的資料庫還不存在時連不上（SQLite 開連線就會建檔，順序無所謂）
        var created = await db.Database.EnsureCreatedAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        if (created)
        {
            await WriteStampAsync(db, expected, ct);
            return;
        }
        if (await ReadStampAsync(db, ct) != expected)
        {
            var connection = db.Database.GetDbConnection();
            var rebuild = db.Database.IsNpgsql() ? $"清空這個 Postgres 資料庫（{connection.Database}）" : "刪掉這個 SQLite 檔";
            throw new InvalidOperationException(
                $"資料庫 schema 跟程式的 model 不一致（{connection.DataSource}）。" +
                $"正式部署前不做 migration：{rebuild}再啟動就會照新 model 重建；" +
                "裡面的登入 cookie、API key 會一起清掉，要重新 bootstrap（匯入 cookie、建 key）。");
        }
    }

    // schema 雜湊記在哪：SQLite 用 PRAGMA user_version；Postgres 沒有對應的東西，記在一張一列的表（不在 EF model 裡，不影響雜湊）
    private const string PostgresStamp = "\"__GatewaySchema\"";

    private static async Task WriteStampAsync(GatewayDbContext db, int version, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
        {
            await ExecuteAsync(db, $"PRAGMA user_version = {version}", ct);
            return;
        }
        await ExecuteAsync(db, $"CREATE TABLE IF NOT EXISTS {PostgresStamp} (\"Version\" integer NOT NULL)", ct);
        await ExecuteAsync(db, $"DELETE FROM {PostgresStamp}", ct);
        await ExecuteAsync(db, $"INSERT INTO {PostgresStamp} VALUES ({version})", ct);
    }

    private static async Task<long?> ReadStampAsync(GatewayDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return await ExecuteAsync(db, "PRAGMA user_version", ct) as long?;
        await ExecuteAsync(db, $"CREATE TABLE IF NOT EXISTS {PostgresStamp} (\"Version\" integer NOT NULL)", ct);
        return await ExecuteAsync(db, $"SELECT \"Version\" FROM {PostgresStamp} LIMIT 1", ct) is int version ? version : null;
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
