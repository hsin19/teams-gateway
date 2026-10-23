using System.Net;
using System.Text;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

public class PeopleDirectoryTests
{
    private static readonly SkypeToken Token = new("skype-token", "live:me", DateTimeOffset.UtcNow.AddHours(8), MtToken: "mt-token");

    [Fact]
    public async Task Fetch_posts_mris_with_both_tokens_and_reads_names()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"type":"Microsoft.SkypeSpaces.MiddleTier.Models.IUserIdentity","value":[
              {"mri":"8:live:alice","displayName":"Alice Chen","givenName":"Alice"},
              {"mri":"8:live:ghost","displayName":""}
            ]}
            """);

        var names = await PeopleDirectory.FetchAsync(new HttpClient(handler), Token, ["8:live:alice", "8:live:ghost"], TestContext.Current.CancellationToken);

        Assert.Equal(new Dictionary<string, string> { ["8:live:alice"] = "Alice Chen" }, names);
        Assert.Equal("""["8:live:alice","8:live:ghost"]""", handler.Body);
        var request = handler.Request!;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.StartsWith("https://teams.live.com/api/mt/beta/users/fetchShortProfile?", request.RequestUri!.ToString());
        Assert.Equal("Bearer mt-token", request.Headers.GetValues("authorization").Single());
        Assert.Equal("skype-token", request.Headers.GetValues("x-skypetoken").Single());
    }

    // 401：回 null 讓呼叫端換 token 重試
    [Fact]
    public async Task Fetch_returns_null_on_unauthorized() =>
        Assert.Null(await PeopleDirectory.FetchAsync(
            new HttpClient(new StubHandler(HttpStatusCode.Unauthorized, "")), Token, ["8:live:alice"], TestContext.Current.CancellationToken));

    // 沒有 MT token 就不打（打了也是 401）
    [Fact]
    public async Task Fetch_without_mt_token_skips_the_call()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"value":[]}""");

        var names = await PeopleDirectory.FetchAsync(new HttpClient(handler), Token with { MtToken = null }, ["8:live:alice"], TestContext.Current.CancellationToken);

        Assert.Empty(names!);
        Assert.Null(handler.Request);
    }

    private sealed class StubHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
