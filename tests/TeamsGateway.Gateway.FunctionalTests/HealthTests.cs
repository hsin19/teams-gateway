using Microsoft.AspNetCore.Hosting;

namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>container healthcheck 打 /health：正式環境（不是只有 Development）也要有，而且不帶細節。</summary>
public sealed class HealthTests(GatewayAppFixture fx) : IClassFixture<GatewayAppFixture>
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Health_endpoints_exist_in_production(string path)
    {
        using var production = fx.WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var resp = await production.CreateClient().GetAsync(path, TestContext.Current.CancellationToken);

        resp.EnsureSuccessStatusCode();
        Assert.Equal("Healthy", await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>/version 回 build 時壓進的版本（版本 + 7 碼 commit），正式環境不用 key 也拿得到。</summary>
    [Fact]
    public async Task Version_endpoint_reports_build_version()
    {
        using var production = fx.WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var resp = await production.CreateClient().GetAsync("/version", TestContext.Current.CancellationToken);

        resp.EnsureSuccessStatusCode();
        Assert.Matches(@"^\d+\.\d+\.\d+.*\+[0-9a-f]{7}$", await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
