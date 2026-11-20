namespace TeamsGateway.Gateway.E2E;

/// <summary>版本門檻怎麼讀目標的 <c>/version</c>：不打 gateway，一般 <c>dotnet test</c> 也會跑。</summary>
public class GatewayVersionTests
{
    // 出版的版本照數字比；本機 build（0.0.0-dev）和看不懂的回 null，門檻不擋
    [Theory]
    [InlineData("0.7.0+cce51a8", "0.7.0")]
    [InlineData("1.2.3-rc.1+abc1234", "1.2.3")]
    [InlineData("0.0.0-dev+abc1234", null)]
    [InlineData("unknown", null)]
    public void Version_text_is_parsed_for_the_gate(string text, string? expected) =>
        Assert.Equal(expected is null ? null : Version.Parse(expected), GatewayTarget.ParseVersion(text));
}
