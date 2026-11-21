using System.Net;
using System.Text;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

/// <summary>只放在 OneDrive 的附件圖：照沒登入的瀏覽器走匿名流程抓原檔。網址、token、id 都是合成的。</summary>
public class OneDriveSharesTests
{
    private const string ShareUrl = "https://1drv.ms/i/c/0123456789abcdef/IQBsyntheticShareToken";
    private const string ShareId = "u!aHR0cHM6Ly8xZHJ2Lm1zL2kvYy8wMTIzNDU2Nzg5YWJjZGVmL0lRQnN5bnRoZXRpY1NoYXJlVG9rZW4";

    // Microsoft Graph 文件〈Encoding sharing URLs〉的範例，加上一個 1drv.ms 的：base64url、不補 =
    [Theory]
    [InlineData(
        "https://onedrive.live.com/redir?resid=1231244193912!12&authKey=1201919!12921!1",
        "u!aHR0cHM6Ly9vbmVkcml2ZS5saXZlLmNvbS9yZWRpcj9yZXNpZD0xMjMxMjQ0MTkzOTEyITEyJmF1dGhLZXk9MTIwMTkxOSExMjkyMSEx")]
    [InlineData(ShareUrl, ShareId)]
    public void ShareId_is_u_bang_plus_unpadded_base64url(string url, string expected) =>
        Assert.Equal(expected, OneDriveShares.ShareId(url));

    // 只代抓 Teams 媒體與 1drv.ms 分享連結，不當開放代理
    [Theory]
    [InlineData("https://us-api.asm.skype.com/v1/objects/0-a/views/imgo", true)]
    [InlineData(ShareUrl, true)]
    [InlineData("http://1drv.ms/i/c/x/abc", false)]
    [InlineData("https://1drv.ms.example.com/i/c/x/abc", false)]
    [InlineData("https://onedrive.live.com/?id=abc", false)]
    [InlineData("https://example.com/x.png", false)]
    public void Media_proxy_accepts_teams_media_and_onedrive_share_links(string url, bool expected) =>
        Assert.Equal(expected, MediaClient.IsAllowed(url, out _));

    // 匿名 token → 兌換分享（Prefer: autoredeem）→ 用 drive / item id 下載原檔，三步都帶 Badger token
    [Fact]
    public async Task Open_redeems_the_share_anonymously_and_downloads_the_original()
    {
        var onedrive = new FakeOneDrive();

        using var resp = await OneDriveShares.OpenAsync(new HttpClient(onedrive), new Uri(ShareUrl), TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("image/png", resp.Content.Headers.ContentType?.MediaType);
        var (token, redeem, content) = (onedrive.Requests[0], onedrive.Requests[1], onedrive.Requests[2]);
        Assert.Equal((HttpMethod.Post, "https://api-badgerp.svc.ms/v1.0/token"), (token.Method, token.Url));
        Assert.Equal("""{"appId":"073204aa-c1e0-4e66-a200-e5815a0aa93d"}""", token.Body);
        Assert.Null(token.Authorization);
        Assert.Equal(HttpMethod.Post, redeem.Method);
        Assert.Equal($"https://my.microsoftpersonalcontent.com/_api/v2.0/shares/{ShareId}/driveitem?$select=id,parentReference", redeem.Url);
        Assert.Equal(("Badger anon-token", "autoredeem"), (redeem.Authorization, redeem.Prefer));
        Assert.Equal(
            (HttpMethod.Get, "https://my.microsoftpersonalcontent.com/_api/v2.1/drives/0123456789ABCDEF/items/0123456789ABCDEF%21sitem/content", "Badger anon-token"),
            (content.Method, content.Url, content.Authorization));
    }

    // 分享不公開（兌換被拒）：丟 InvalidOperationException，/v1/media 回 502，relay 退回貼連結
    [Fact]
    public async Task Open_fails_when_the_share_cannot_be_redeemed()
    {
        var onedrive = new FakeOneDrive { RedeemStatus = HttpStatusCode.Forbidden };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OneDriveShares.OpenAsync(new HttpClient(onedrive), new Uri(ShareUrl), TestContext.Current.CancellationToken));

        Assert.Equal("Redeeming the OneDrive share failed: HTTP 403", ex.Message);
        Assert.Equal(2, onedrive.Requests.Count);
    }

    private sealed record SeenRequest(HttpMethod Method, string Url, string? Authorization, string? Prefer, string? Body);

    /// <summary>假的 badger 與 OneDrive API：記下收到的請求，照網址回固定內容。</summary>
    private sealed class FakeOneDrive : HttpMessageHandler
    {
        public HttpStatusCode RedeemStatus { get; init; } = HttpStatusCode.OK;
        public List<SeenRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(new SeenRequest(
                request.Method,
                url,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Prefer", out var prefer) ? prefer.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            if (url.StartsWith("https://api-badgerp.svc.ms/", StringComparison.Ordinal))
                return Json("""{"token":"anon-token","expiryTimeUtc":"2026-11-21T10:00:00Z","authScheme":"Badger"}""");
            if (url.Contains("/driveitem", StringComparison.Ordinal))
                return RedeemStatus == HttpStatusCode.OK
                    ? Json("""{"id":"0123456789ABCDEF!sitem","parentReference":{"driveId":"0123456789ABCDEF","driveType":"personal"}}""")
                    : new HttpResponseMessage(RedeemStatus);
            var png = new ByteArrayContent([1, 2, 3]);
            png.Headers.ContentType = new("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = png };
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
