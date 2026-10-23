using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.UnitTests;

/// <summary>每個測試一個獨立的 in-memory SQLite DB（連線開著才活）；當 IDbContextFactory 用。schema 走正式的 migration。</summary>
public sealed class TestDb : IDbContextFactory<GatewayDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<SqliteGatewayDbContext> _options;

    public TestDb(bool migrate = true)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<SqliteGatewayDbContext>().UseSqlite(_connection).Options;
        if (migrate)
            DataExtensions.MigrateAsync(this, CancellationToken.None).GetAwaiter().GetResult();
    }

    public GatewayDbContext CreateDbContext() => new SqliteGatewayDbContext(_options);

    public void Dispose() => _connection.Dispose();
}
