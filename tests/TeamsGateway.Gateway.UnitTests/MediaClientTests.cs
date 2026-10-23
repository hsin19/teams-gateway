using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

public class MediaClientTests
{
    [Fact]
    public async Task Reads_body_within_limit()
    {
        var bytes = new byte[1000];
        Random.Shared.NextBytes(bytes);

        var read = await MediaClient.ReadBoundedAsync(new StreamContent(new MemoryStream(bytes)), 1000, TestContext.Current.CancellationToken);

        Assert.Equal(bytes, read);
    }

    // 沒有（或謊報）Content-Length 時，只靠讀的時候數
    [Fact]
    public async Task Stops_reading_past_the_limit() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MediaClient.ReadBoundedAsync(new StreamContent(new MemoryStream(new byte[200_000])), 100_000, TestContext.Current.CancellationToken));
}
