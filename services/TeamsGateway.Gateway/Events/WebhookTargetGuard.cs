using System.Net;
using System.Net.Sockets;

namespace TeamsGateway.Gateway.Events;

/// <summary>
/// webhook 的目標網址是 API key 持有者自己填的，gateway 會照著 POST 訊息內容：不設限就能拿 gateway 當跳板打內網、
/// 本機服務或雲端 metadata（169.254.169.254）。檢查做在「真的要連線」那一刻、對解析出來的 IP 做，
/// 所以 DNS rebinding（登記時解析到外網、送的時候換成內網）也擋得住。
/// </summary>
public static class WebhookTargetGuard
{
    public static bool IsAllowedScheme(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;

    /// <summary>
    /// link-local（含雲端 metadata）、未指定位址、multicast / 保留段一律不行；
    /// loopback 與私有網段（10/8、172.16/12、192.168/16、100.64/10、IPv6 ULA）要 <paramref name="allowPrivate"/> 才行。
    /// </summary>
    public static bool IsAllowedAddress(IPAddress address, bool allowPrivate)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return allowPrivate;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0 || (b[0] == 169 && b[1] == 254) || b[0] >= 224)
                return false;
            var isPrivate = b[0] == 10
                            || (b[0] == 172 && b[1] is >= 16 and <= 31)
                            || (b[0] == 192 && b[1] == 168)
                            || (b[0] == 100 && b[1] is >= 64 and <= 127);
            return !isPrivate || allowPrivate;
        }

        if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast)
            return false;
        if (address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal)
            return allowPrivate;
        return true;
    }

    /// <summary>webhook HttpClient 的 ConnectCallback：解析 DNS、濾掉不允許的位址，連剩下的；全被濾掉就拒絕。</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, bool allowPrivate, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(a => IsAllowedAddress(a, allowPrivate)).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException(
                $"webhook 目標 {host} 解析到不允許的位址（{string.Join(", ", addresses.Select(a => a.ToString()))}）；" +
                "內網 / loopback 要設 Gateway:WebhookAllowPrivateNetworks=true");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
