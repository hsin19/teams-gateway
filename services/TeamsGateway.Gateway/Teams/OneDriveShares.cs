using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TeamsGateway.Gateway.Teams;

/// <summary>
/// 抓個人版 OneDrive「知道連結的人都能看」的分享檔，走的是沒登入的瀏覽器開 <c>1drv.ms</c> 的匿名流程
/// （spike/FINDINGS.md〈OneDrive 分享檔〉）：跟 badger 換一把匿名 token → 用它兌換分享連結 → 下載原檔。
/// Teams 貼上的圖有時只放在發送者的 OneDrive、附件沒有預覽圖，靠這個才轉得出去。分享不公開時兌換會失敗。
/// 這些是逆向網頁版得來的非官方端點，Microsoft 隨時可能改。
/// </summary>
public static class OneDriveShares
{
    internal const string BadgerTokenUrl = "https://api-badgerp.svc.ms/v1.0/token";

    /// <summary>OneDrive 網頁檢視器匿名讀分享用的 app id（抓自網頁版，不是 secret）。</summary>
    internal const string AppId = "073204aa-c1e0-4e66-a200-e5815a0aa93d";

    internal const string Api = "https://my.microsoftpersonalcontent.com/_api";

    // 照網頁版帶：這些端點是給 onedrive.live.com 用的
    private const string WebOrigin = "https://onedrive.live.com";

    public static bool IsShareLink(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("1drv.ms", StringComparison.OrdinalIgnoreCase);

    /// <summary>分享網址 → API 用的 share id：<c>u!</c> 加上整個網址的 base64url（不補 <c>=</c>），跟 Teams 給的 <c>fileInfo.shareId</c> 一樣。</summary>
    public static string ShareId(string shareUrl) => "u!" + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(shareUrl));

    /// <summary>下載分享的原檔，回傳只讀完 header 的 response（body 由呼叫端限量讀）。兌換不到時丟 <see cref="InvalidOperationException"/>。</summary>
    public static async Task<HttpResponseMessage> OpenAsync(HttpClient http, Uri shareUrl, CancellationToken ct)
    {
        var token = await BadgerTokenAsync(http, ct);
        var (driveId, itemId) = await RedeemAsync(http, token, ShareId(shareUrl.OriginalString), ct);
        var req = Request(HttpMethod.Get, $"{Api}/v2.1/drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}/content", token);
        return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<string> BadgerTokenAsync(HttpClient http, CancellationToken ct)
    {
        var req = Request(HttpMethod.Post, BadgerTokenUrl, badgerToken: null);
        req.Content = new StringContent($$"""{"appId":"{{AppId}}"}""", Encoding.UTF8, "application/json");
        using var doc = await SendForJsonAsync(http, req, "Getting an anonymous OneDrive token", ct);
        return doc.RootElement.TryGetProperty("token", out var token) && token.GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException("Getting an anonymous OneDrive token failed: no token in the response");
    }

    /// <summary>兌換分享連結（<c>Prefer: autoredeem</c>），拿到檔案所在的 drive 與 item id。</summary>
    private static async Task<(string DriveId, string ItemId)> RedeemAsync(HttpClient http, string badgerToken, string shareId, CancellationToken ct)
    {
        var req = Request(HttpMethod.Post, $"{Api}/v2.0/shares/{shareId}/driveitem?$select=id,parentReference", badgerToken);
        req.Headers.TryAddWithoutValidation("Prefer", "autoredeem");
        using var doc = await SendForJsonAsync(http, req, "Redeeming the OneDrive share", ct);
        var root = doc.RootElement;
        return root.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } itemId
            && root.TryGetProperty("parentReference", out var parent) && parent.TryGetProperty("driveId", out var drive)
            && drive.GetString() is { Length: > 0 } driveId
            ? (driveId, itemId)
            : throw new InvalidOperationException("Redeeming the OneDrive share failed: no item id in the response");
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? badgerToken)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Origin", WebOrigin);
        req.Headers.Referrer = new Uri(WebOrigin + "/");
        if (badgerToken is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Badger", badgerToken);
        return req;
    }

    private static async Task<JsonDocument> SendForJsonAsync(HttpClient http, HttpRequestMessage req, string what, CancellationToken ct)
    {
        using (req)
        using (var resp = await http.SendAsync(req, ct))
        {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{what} failed: HTTP {(int)resp.StatusCode}");
            try
            {
                return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"{what} failed: the response isn't JSON");
            }
        }
    }
}
