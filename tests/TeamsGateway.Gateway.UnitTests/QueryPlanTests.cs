using Microsoft.EntityFrameworkCore;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

/// <summary>
/// 常跑、表又會一直長的查詢要走索引：用 SQLite 的 <c>EXPLAIN QUERY PLAN</c> 確認是照索引找（<c>SEARCH</c>），不是整個走一遍（<c>SCAN</c>）。
/// </summary>
public class QueryPlanTests
{
    // 每次有人按表情都要查按的人叫什麼：MessageEvents 永遠不清，整張掃會越來越慢
    [Fact]
    public async Task Reactor_name_lookup_uses_an_index()
    {
        using var db = new TestDb();
        await using var ctx = db.CreateDbContext();

        var plan = await PlanAsync(ctx, MessageIngestor.LatestSenderNameQuery(ctx, "8:alice"));

        AssertSeeks(plan);
    }

    // SSE 帶 afterMessage 續接（relay 每次重啟）：照 Teams 訊息 id 找它第一次發出的事件
    [Fact]
    public async Task Event_lookup_by_message_id_uses_an_index()
    {
        using var db = new TestDb();
        await using var ctx = db.CreateDbContext();

        var plan = await PlanAsync(ctx, ctx.PublishedEvents.Where(e => e.MessageId == "1792640000000").Select(e => e.Seq));

        AssertSeeks(plan);
    }

    /// <summary>每一步都是照索引找（SEARCH），不是整張表或整個索引走一遍（SCAN，表名是 EF 的別名，所以不比表名）。</summary>
    private static void AssertSeeks(string plan) =>
        Assert.DoesNotContain(plan.Split('\n'), step => step.StartsWith("SCAN", StringComparison.Ordinal));

    private static async Task<string> PlanAsync<T>(GatewayDbContext ctx, IQueryable<T> query)
    {
        var ct = TestContext.Current.CancellationToken;
        await ctx.Database.OpenConnectionAsync(ct);
        await using var command = query.CreateDbCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var details = new List<string>();
        while (await reader.ReadAsync(ct))
            details.Add(reader.GetString(reader.GetOrdinal("detail")));
        return string.Join("\n", details);
    }
}
