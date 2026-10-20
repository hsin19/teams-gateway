using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Tests;

/// <summary>每個測試一個獨立的 in-memory SQLite DB（連線開著才活）；當 IDbContextFactory 用。</summary>
public sealed class TestDb : IDbContextFactory<GatewayDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<GatewayDbContext> _options;

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(_connection).Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public GatewayDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
