using TeamsGateway.Gateway.Auth;

namespace TeamsGateway.Gateway.Tests;

public class ApiKeyServiceTests
{
    [Fact]
    public async Task Create_then_validate_roundtrips()
    {
        using var db = new TestDb();
        var service = new ApiKeyService(db);

        var (key, raw) = await service.CreateAsync("client", TestContext.Current.CancellationToken);

        Assert.StartsWith("tgw_", raw);
        var validated = await service.ValidateAsync(raw, TestContext.Current.CancellationToken);
        Assert.NotNull(validated);
        Assert.Equal(key.Id, validated!.Id);
        Assert.Equal("client", validated.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("tgw_wrong")]
    public async Task Validate_rejects_bad_keys(string raw)
    {
        using var db = new TestDb();
        var service = new ApiKeyService(db);
        await service.CreateAsync("client", TestContext.Current.CancellationToken);

        Assert.Null(await service.ValidateAsync(raw, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Revoked_key_no_longer_validates()
    {
        using var db = new TestDb();
        var service = new ApiKeyService(db);
        var (key, raw) = await service.CreateAsync("client", TestContext.Current.CancellationToken);

        Assert.True(await service.RevokeAsync(key.Id, TestContext.Current.CancellationToken));

        Assert.Null(await service.ValidateAsync(raw, TestContext.Current.CancellationToken));
        Assert.False(await service.RevokeAsync(key.Id, TestContext.Current.CancellationToken)); // 重複撤銷
    }

    [Fact]
    public async Task List_orders_by_created_at()
    {
        // 回歸：ListAsync 用 OrderBy(CreatedAt)，CreatedAt 是 DateTimeOffset。
        // SQLite 不能對 DateTimeOffset 做 ORDER BY，converter 沒設好這裡會丟 500。
        using var db = new TestDb();
        var service = new ApiKeyService(db);
        await service.CreateAsync("a", TestContext.Current.CancellationToken);
        await service.CreateAsync("b", TestContext.Current.CancellationToken);

        var all = await service.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], all.Select(k => k.Name));
    }
}
