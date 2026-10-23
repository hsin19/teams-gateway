using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TeamsGateway.Gateway.Data;

// dotnet ef 產生 migration 用（見 tools/db-migration.sh）：有它就不用啟動整個 gateway。
// 產生 migration 只比對 model，不連 DB，連線字串只是讓 provider 起得來。

public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteGatewayDbContext>
{
    public SqliteGatewayDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteGatewayDbContext>().UseSqlite("Data Source=design-time.db").Options);
}

public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresGatewayDbContext>
{
    public PostgresGatewayDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresGatewayDbContext>().UseNpgsql("Host=localhost").Options);
}
