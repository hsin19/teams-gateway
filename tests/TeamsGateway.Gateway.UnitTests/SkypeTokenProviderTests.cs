using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

public class SkypeTokenProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly SkypeToken Fresh = new("new-token", "live:me", Now.AddHours(20));

    // 好幾個請求同時 401：第一個重換完，後面排隊的拿到的已經是新 token，直接用
    [Fact]
    public void Reuses_token_someone_else_just_minted() =>
        Assert.True(SkypeTokenProvider.CanReuse(Fresh, rejectedToken: "old-token", Now));

    [Fact]
    public void Remints_when_cached_token_is_the_rejected_one() =>
        Assert.False(SkypeTokenProvider.CanReuse(Fresh, rejectedToken: "new-token", Now));

    [Fact]
    public void Remints_when_cached_token_is_about_to_expire() =>
        Assert.False(SkypeTokenProvider.CanReuse(Fresh with { ExpiresAt = Now.AddMinutes(5) }, rejectedToken: "old-token", Now));

    // admin 強制換（沒指定被拒的）一律重換
    [Fact]
    public void Forced_refresh_always_remints() =>
        Assert.False(SkypeTokenProvider.CanReuse(Fresh, rejectedToken: null, Now));

    [Fact]
    public void Remints_without_cache() =>
        Assert.False(SkypeTokenProvider.CanReuse(null, rejectedToken: "old-token", Now));
}
