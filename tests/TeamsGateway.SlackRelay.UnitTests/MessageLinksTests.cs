using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

public class MessageLinksTests
{
    private static string Ts(DateTimeOffset at) => $"{at.ToUnixTimeSeconds()}.000100";

    [Fact]
    public void Prune_drops_only_old_links()
    {
        var now = DateTimeOffset.UtcNow;
        var links = new MessageLinks();
        links.Remember("old", "C1", Ts(now.AddDays(-8)), "old text", 1);
        links.Remember("new", "C1", Ts(now.AddHours(-1)), "new text", 2);
        links.Remember("weird", "C1", "not-a-ts", "?", 3); // 看不懂的 ts 不刪

        links.PruneOlderThan(now - MessageLinks.MaxAge);

        Assert.Null(links.Lookup("old"));
        Assert.NotNull(links.Lookup("new"));
        Assert.NotNull(links.Lookup("weird"));
    }

    [Fact]
    public void NewestTeamsId_is_the_latest_slack_post()
    {
        var now = DateTimeOffset.UtcNow;
        var links = new MessageLinks();
        Assert.Null(links.NewestTeamsId());

        links.Remember("a", "C1", Ts(now.AddHours(-2)), "x", 1);
        links.Remember("b", "C2", Ts(now.AddMinutes(-5)), "x", 2);
        links.Remember("c", "C1", Ts(now.AddHours(-1)), "x", 3);
        links.Remember("weird", "C1", "not-a-ts", "x", 4);

        Assert.Equal("b", links.NewestTeamsId());
    }

    // 常駐很久也不會一直長：記到一定量會自動清舊的
    [Fact]
    public void Remember_prunes_periodically()
    {
        var links = new MessageLinks();
        links.Remember("old", "C1", Ts(DateTimeOffset.UtcNow.AddDays(-30)), "x", 1);
        for (var i = 0; i < 600; i++)
            links.Remember($"m{i}", "C1", Ts(DateTimeOffset.UtcNow), "x", i);

        Assert.Null(links.Lookup("old"));
        Assert.Equal(600, links.Count);
    }
}
