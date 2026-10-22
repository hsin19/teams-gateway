namespace TeamsGateway.SlackRelay;

/// <summary>透過 gateway 的 <c>/v1/media</c> 代抓 Teams 內嵌圖片（relay 自己沒有 Teams 登入）。</summary>
public sealed class GatewayMediaClient(HttpClient http, ILogger<GatewayMediaClient> logger)
{
    public async Task<(byte[] Bytes, string ContentType)?> GetAsync(string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"/v1/media?url={Uri.EscapeDataString(url)}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("代抓圖片失敗 HTTP {Status}：{Url}", (int)resp.StatusCode, url);
            return null;
        }
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return (bytes, resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }
}
