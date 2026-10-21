using System.Net.Http.Json;
using TeamsGateway.Contracts;

namespace TeamsGateway.Gateway.E2E;

/// <summary>
/// 對實際在跑的 gateway 打真帳號、驗跟 Teams 的溝通沒被它改版打爛的 canary。
/// 每天定期跑（GitHub cron），失敗代表非官方 API 可能變了，要去看。
/// 沒配置 live 目標（<c>GATEWAY_API_KEY</c> 未設）時整組 skip，所以一般 CI 不會誤跑。
/// </summary>
public sealed class CanaryTests(GatewayFixture fx) : IClassFixture<GatewayFixture>
{
    private const string NotConfigured = "未設定 GATEWAY_API_KEY，略過 live E2E canary";

    /// <summary>
    /// 列對話成功，就隱含證明了：skypetoken 換發正常（打得到 chatsvc）、chatsvc 讀路徑正常、
    /// 回傳形狀還能反序列化進我們的 wire DTO（Teams 改 schema 會在這裡爆）。
    /// </summary>
    [Fact]
    public async Task Chats_lists_conversations_with_expected_shape()
    {
        Assert.SkipUnless(fx.Configured, NotConfigured);
        var ct = TestContext.Current.CancellationToken;

        var chats = await fx.Client.GetFromJsonAsync<IReadOnlyList<ConversationDto>>("/v1/chats", ct);

        Assert.NotNull(chats);
        Assert.NotEmpty(chats);
        Assert.All(chats, c => Assert.False(string.IsNullOrWhiteSpace(c.Id)));
    }

    /// <summary>
    /// 發一則帶唯一標記的訊息到自測群，再從 Teams 讀回同一則，驗 send + 讀回整條活著。
    /// Teams 寫入到讀得到有延遲，poll 幾次。
    /// </summary>
    [Fact]
    public async Task Send_then_read_round_trips_through_teams()
    {
        Assert.SkipUnless(fx.Configured, NotConfigured);
        var ct = TestContext.Current.CancellationToken;
        var marker = $"e2e-canary {Guid.NewGuid():N}";

        var send = await fx.Client.PostAsJsonAsync($"/v1/chats/{fx.Chat}/messages", new SendMessageRequest(marker), ct);
        send.EnsureSuccessStatusCode();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var page = await fx.Client.GetFromJsonAsync<MessagePage>($"/v1/chats/{fx.Chat}/messages?limit=20", ct);
            if (page is not null && page.Messages.Any(m => m.Content == marker))
                return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        Assert.Fail($"送出的訊息「{marker}」在時限內沒有從 Teams 讀回");
    }
}
