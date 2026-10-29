using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using TeamsGateway.Contracts;

namespace TeamsGateway.Gateway.E2E;

/// <summary>
/// 對實際在跑的 gateway 打真帳號、驗跟 Teams 的溝通沒被它改版打爛的 canary。
/// 每天定期跑（GitHub cron），失敗代表非官方 API 可能變了，要去看。
/// 沒配置 live 目標（<c>GATEWAY_API_KEY</c> 未設）時整組 skip（<see cref="LiveFactAttribute"/>），所以一般 CI 不會誤跑。
/// </summary>
public sealed class CanaryTests(GatewayFixture fx) : IClassFixture<GatewayFixture>
{
    /// <summary>
    /// 列對話成功，就隱含證明了：skypetoken 換發正常（打得到 chatsvc）、chatsvc 讀路徑正常、
    /// 回傳形狀還能反序列化進我們的 wire DTO（Teams 改 schema 會在這裡爆）。
    /// </summary>
    [LiveFact]
    public async Task Chats_lists_conversations_with_expected_shape()
    {
        var ct = TestContext.Current.CancellationToken;

        var chats = await fx.Client.GetFromJsonAsync<IReadOnlyList<ConversationDto>>("/v1/chats", ct);

        Assert.NotNull(chats);
        Assert.NotEmpty(chats);
        Assert.All(chats, c => Assert.False(string.IsNullOrWhiteSpace(c.Id)));
    }

    /// <summary>
    /// 先連上 SSE 再發一則帶唯一標記的訊息到自測群：驗即時推播（Trouter → SSE）和 send + 讀回整條活著。
    /// 一則訊息驗兩條路，不多發。自測群要在 gateway 上訂閱，不然進不了事件串流。
    /// </summary>
    [LiveFact]
    public async Task Send_is_pushed_over_sse_and_read_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var marker = $"e2e-canary {Guid.NewGuid():N}";

        // 收到 header 時 gateway 已經定好游標，之後才進來的事件一定會送到
        using var events = await fx.Client.GetAsync(
            $"/v1/events?chats={Uri.EscapeDataString(fx.Chat)}", HttpCompletionOption.ResponseHeadersRead, ct);
        events.EnsureSuccessStatusCode();
        var pushed = WaitForMessageAsync(await events.Content.ReadAsStreamAsync(ct), marker, ct);

        var send = await fx.Client.PostAsJsonAsync($"/v1/chats/{fx.Chat}/messages", new SendMessageRequest(marker), ct);
        send.EnsureSuccessStatusCode();

        // 輪詢會把 10 秒內的新訊息留給 Trouter（PollService 的 Grace）：這段時間內推到，才是 Trouter 推的、不是輪詢補的
        try
        {
            await pushed.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Message '{marker}' was not pushed over SSE within 10s; Trouter push may be broken (polling would only catch it later)");
        }

        // Teams 寫入到讀得到有延遲，poll 幾次
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var page = await fx.Client.GetFromJsonAsync<MessagePage>($"/v1/chats/{fx.Chat}/messages?limit=20", ct);
            if (page is not null && page.Messages.Any(m => m.Content == marker))
                return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        Assert.Fail($"Message '{marker}' was not read back from Teams in time");
    }

    private static async Task WaitForMessageAsync(Stream stream, string marker, CancellationToken ct)
    {
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(ct))
        {
            if (item.EventType != EventKinds.Message)
                continue;
            var message = JsonSerializer.Deserialize<MessageDto>(item.Data, JsonSerializerOptions.Web);
            if (message?.Content == marker)
                return;
        }
        throw new InvalidOperationException("SSE stream ended before the message arrived");
    }
}
