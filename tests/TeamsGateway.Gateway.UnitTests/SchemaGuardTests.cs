using Microsoft.EntityFrameworkCore;
using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.UnitTests;

/// <summary>正式部署前不做 migration：model 改過、DB 還是舊的時候要在啟動時擋下來，而不是跑到一半噴 no such column。</summary>
public class SchemaGuardTests
{
    [Fact]
    public async Task Created_database_is_stamped_and_accepted_again()
    {
        using var db = new TestDb(); // 建立時已經跑過一次
        var ct = TestContext.Current.CancellationToken;

        Assert.NotEqual(0L, await UserVersionAsync(db, ct));
        await DataExtensions.EnsureSchemaAsync(db, ct); // 同一個 model 再跑：沒事
    }

    // model 改過（雜湊不同），或是舊版用 migration 建的 DB（user_version = 0）
    [Theory]
    [InlineData(0)]
    [InlineData(12345)]
    public async Task Stale_schema_refuses_to_start_with_instructions(long staleVersion)
    {
        using var db = new TestDb();
        var ct = TestContext.Current.CancellationToken;
        var stamp = "PRAGMA user_version = " + staleVersion; // PRAGMA 不吃參數
        await using (var ctx = db.CreateDbContext())
            await ctx.Database.ExecuteSqlRawAsync(stamp, ct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DataExtensions.EnsureSchemaAsync(db, ct));

        Assert.Contains("刪掉這個 SQLite 檔", ex.Message);
    }

    private static async Task<long> UserVersionAsync(TestDb db, CancellationToken ct)
    {
        await using var ctx = db.CreateDbContext();
        var connection = ctx.Database.GetDbConnection();
        await ctx.Database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
