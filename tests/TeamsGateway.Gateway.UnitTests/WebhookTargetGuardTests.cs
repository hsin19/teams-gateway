using System.Net;
using TeamsGateway.Gateway.Events;

namespace TeamsGateway.Gateway.UnitTests;

public class WebhookTargetGuardTests
{
    [Theory]
    [InlineData("93.184.216.34", false, true)]     // 外網
    [InlineData("2606:2800:220:1::1", false, true)]
    [InlineData("169.254.169.254", true, false)]   // 雲端 metadata：開了內網也不行
    [InlineData("fe80::1", true, false)]
    [InlineData("0.0.0.0", true, false)]
    [InlineData("::", true, false)]
    [InlineData("224.0.0.1", true, false)]
    [InlineData("255.255.255.255", true, false)]
    [InlineData("127.0.0.1", false, false)]        // loopback / 私有網段：要開設定
    [InlineData("127.0.0.1", true, true)]
    [InlineData("::1", false, false)]
    [InlineData("::ffff:10.0.0.5", false, false)]  // IPv4-mapped 不能繞過
    [InlineData("10.0.0.5", false, false)]
    [InlineData("10.0.0.5", true, true)]
    [InlineData("172.20.0.3", false, false)]       // docker 網路
    [InlineData("172.32.0.1", false, true)]
    [InlineData("192.168.1.10", false, false)]
    [InlineData("100.100.1.1", false, false)]      // CGNAT / Tailscale
    [InlineData("fd12:3456::1", false, false)]
    [InlineData("fd12:3456::1", true, true)]
    public void Address_rules(string ip, bool allowPrivate, bool expected) =>
        Assert.Equal(expected, WebhookTargetGuard.IsAllowedAddress(IPAddress.Parse(ip), allowPrivate));

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1:1/hook")]
    [InlineData("http://localhost:1/hook")] // 主機名要解析後再檢查
    public async Task Blocked_targets_fail_before_connecting(string url)
    {
        using var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = (context, ct) => WebhookTargetGuard.ConnectAsync(context, allowPrivate: false, ct),
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.PostAsync(url, new StringContent("{}"), TestContext.Current.CancellationToken));
        Assert.Contains("不允許的位址", ex.ToString());
    }
}
