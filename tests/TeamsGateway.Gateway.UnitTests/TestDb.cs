using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.UnitTests;

/// <summary>每個測試一個獨立的 in-memory SQLite DB（連線開著才活）；當 IDbContextFactory 用。schema 走正式的 migration。</summary>
public sealed class TestDb : IDbContextFactory<GatewayDbContext>, IDisposable
{
    private readonly SqliteConnection? _connection;
    private readonly string? _path;
    private readonly DbContextOptions<SqliteGatewayDbContext> _options;

    public TestDb(bool migrate = true)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<SqliteGatewayDbContext>().UseSqlite(_connection).Options;
        if (migrate)
            DataExtensions.MigrateAsync(this, CancellationToken.None).GetAwaiter().GetResult();
    }

    private TestDb(string path)
    {
        _path = path;
        // 不用 pooling：測試結束要刪得掉檔案
        _options = new DbContextOptionsBuilder<SqliteGatewayDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        DataExtensions.MigrateAsync(this, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 暫存檔上的 DB，每個 context 各開一條連線，跟正式環境一樣。測併發要用它：in-memory 版所有 context 共用一條連線，
    /// 而一條連線不能同時給多個執行緒用。
    /// </summary>
    public static TestDb OnDisk() => new(Path.Combine(Path.GetTempPath(), $"teams-gateway-test-{Guid.NewGuid():N}.db"));

    public GatewayDbContext CreateDbContext() => new SqliteGatewayDbContext(_options);

    public void Dispose()
    {
        _connection?.Dispose();
        if (_path is not null)
            foreach (var file in new[] { _path, _path + "-wal", _path + "-shm", _path + "-journal" })
                File.Delete(file);
    }
}
