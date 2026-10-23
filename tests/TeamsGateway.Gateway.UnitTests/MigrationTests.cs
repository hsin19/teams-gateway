using Microsoft.EntityFrameworkCore;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.UnitTests;

/// <summary>schema 走 EF migration（ADR 0009），SQLite、Postgres 各一套：model 改了卻忘了產生 migration 要在這裡就失敗。</summary>
public class MigrationTests
{
    // 忘了跑 tools/db-migration.sh add：兩個 provider 都要跟上 model
    [Fact]
    public void Migrations_are_up_to_date_with_the_model()
    {
        using var sqlite = new SqliteDesignTimeFactory().CreateDbContext([]);
        using var postgres = new PostgresDesignTimeFactory().CreateDbContext([]); // 只比對 model，不連 DB

        Assert.False(sqlite.Database.HasPendingModelChanges(), "SQLite 的 migration 沒跟上 model：跑 tools/db-migration.sh add <名稱>");
        Assert.False(postgres.Database.HasPendingModelChanges(), "Postgres 的 migration 沒跟上 model：跑 tools/db-migration.sh add <名稱>");
    }

    // 新的 DB 照 migration 建；已經是最新的再跑一次沒事（每次啟動都會跑）
    [Fact]
    public async Task Fresh_database_is_migrated_and_migrating_again_is_a_no_op()
    {
        using var db = new TestDb(); // 建立時已經跑過一次
        var ct = TestContext.Current.CancellationToken;

        await DataExtensions.MigrateAsync(db, ct);

        await using var ctx = db.CreateDbContext();
        Assert.Contains((await ctx.Database.GetAppliedMigrationsAsync(ct)), m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
        Assert.Empty(await ctx.Database.GetPendingMigrationsAsync(ct));
    }

    // 導入 migration 之前用 EnsureCreated 建的 DB：有表沒紀錄，直接套會撞「表已經存在」，要停下來說怎麼接
    [Fact]
    public async Task Database_created_without_migrations_refuses_with_instructions()
    {
        using var db = new TestDb(migrate: false);
        var ct = TestContext.Current.CancellationToken;
        await using (var ctx = db.CreateDbContext())
            await ctx.Database.EnsureCreatedAsync(ct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DataExtensions.MigrateAsync(db, ct));

        Assert.Contains("基線紀錄", ex.Message);
    }
}
