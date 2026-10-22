using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace TeamsGateway.Gateway.Teams;

/// <summary>
/// 代抓 Teams 訊息裡的內嵌圖片（AMSImage，放在 <c>*.asm.skype.com</c>）。這些網址要 Teams 登入才拿得到，
/// 外部（Slack 等）沒辦法直接顯示，所以由 gateway 用自己的 skypetoken 抓。只接受這個網域，不當開放代理。
/// </summary>
public sealed class MediaClient(HttpClient http, SkypeTokenProvider tokens)
{
    private const long MaxBytes = 25 * 1024 * 1024;

    public static bool IsAllowed(string? url, [NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.EndsWith(".asm.skype.com", StringComparison.OrdinalIgnoreCase);

    public async Task<(byte[] Bytes, string ContentType)> GetAsync(Uri uri, CancellationToken ct)
    {
        var token = await tokens.GetAsync(ct);
        var resp = await SendAsync(uri, token.Token, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            token = await tokens.RefreshAsync(ct);
            resp = await SendAsync(uri, token.Token, ct);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"抓媒體失敗 HTTP {(int)resp.StatusCode}");
            if (resp.Content.Headers.ContentLength > MaxBytes)
                throw new InvalidOperationException($"媒體太大（{resp.Content.Headers.ContentLength} bytes）");
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return (bytes, resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
        }
    }

    private Task<HttpResponseMessage> SendAsync(Uri uri, string skypeToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("Authorization", $"skype_token {skypeToken}");
        return http.SendAsync(req, ct);
    }
}
