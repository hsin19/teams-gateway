using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>發話者與內文：不管單行還是多行，內文都另起一行，版面一致。</summary>
public class ComposeTests
{
    [Theory]
    [InlineData("hi", "*Alice*:\nhi")]
    [InlineData("line 1\nline 2", "*Alice*:\nline 1\nline 2")]
    [InlineData("> *Bob*\n> quoted\nreply", "*Alice*:\n> *Bob*\n> quoted\nreply")]
    [InlineData("", "*Alice*:")] // 只有圖 / 附件
    public void Body_always_starts_on_its_own_line(string body, string expected) =>
        Assert.Equal(expected, RelayWorker.Compose("*Alice*:", body));
}
