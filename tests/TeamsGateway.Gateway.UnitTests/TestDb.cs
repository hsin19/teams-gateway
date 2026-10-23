using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.UnitTests;

/// <summary>每個測試一個獨立的 in-memory SQLite DB（連線開著才活）；當 IDbContextFactory 用。schema 走正式的建立流程。</summary>
public sealed class TestDb : IDbContextFactory<GatewayDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<GatewayDbContext> _options;

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<GatewayDbContext>().UseSqlite(_connection).Options;
        DataExtensions.EnsureSchemaAsync(this, CancellationToken.None).GetAwaiter().GetResult();
    }

    public GatewayDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
