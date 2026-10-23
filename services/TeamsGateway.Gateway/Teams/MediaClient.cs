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
            token = await tokens.RefreshAsync(token.Token, ct);
            resp = await SendAsync(uri, token.Token, ct);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"抓媒體失敗 HTTP {(int)resp.StatusCode}");
            if (resp.Content.Headers.ContentLength > MaxBytes)
                throw new InvalidOperationException($"媒體太大（{resp.Content.Headers.ContentLength} bytes）");
            var bytes = await ReadBoundedAsync(resp.Content, MaxBytes, ct);
            return (bytes, resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
        }
    }

    /// <summary>邊讀邊數，超過上限就停（沒有 Content-Length、或它說謊時，上面的檢查擋不住）。</summary>
    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidOperationException($"媒體太大（超過 {maxBytes} bytes）");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // 只讀 header 就回來：預設會先把整個 body 讀進記憶體，上限檢查就來不及了
    private Task<HttpResponseMessage> SendAsync(Uri uri, string skypeToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("Authorization", $"skype_token {skypeToken}");
        return http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}
