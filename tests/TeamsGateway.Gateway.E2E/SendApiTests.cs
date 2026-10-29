using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Threading.Channels;
using TeamsGateway.Contracts;

namespace TeamsGateway.Gateway.E2E;

/// <summary>
/// 發話 API 的選填欄位對真的 Teams 跑一次（ADR 0012）：<c>html</c>、自訂 <c>clientMessageId</c>、<c>replyToId</c> 引用回覆，
/// 以及回應裡 Teams 給的 <c>messageId</c>。這些都是逆向來的格式，Teams 改了就會在這裡爆。
/// 每次發兩則到自測群：第一則 HTML，第二則引用它。
/// </summary>
public sealed class SendApiTests(GatewayFixture fx) : IClassFixture<GatewayFixture>
{
    private const string NotConfigured = "GATEWAY_API_KEY is not set; skipping live E2E canary";

    // 推回來不一定是 Trouter 推的（輪詢也會補），這裡不驗推播，等久一點
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 第一則 HTML、帶跟 Slack ts 同格式的 16 碼 clientMessageId：回應的 messageId 就是推回來那則的 id，
    /// 推回來時 clientMessageId 原樣、型別是 RichText/Html、格式標籤還在。
    /// 第二則用 replyToId 引用第一則：內容前面有 Reply blockquote，而且 Teams 把 qtdMsgs 驗成 Valid（真的當成回覆，不是只畫得出引用）。
    /// </summary>
    [Fact]
    public async Task Html_send_and_quote_reply_round_trip()
    {
        Assert.SkipUnless(fx.Configured, NotConfigured);
        var ct = TestContext.Current.CancellationToken;
        var marker = Guid.NewGuid().ToString("N");
        var clientMessageId = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{Random.Shared.Next(1_000_000):D6}");

        // 收到 header 時 gateway 已經定好游標，之後才進來的事件一定會送到
        using var events = await fx.Client.GetAsync(
            $"/v1/events?chats={Uri.EscapeDataString(fx.Chat)}", HttpCompletionOption.ResponseHeadersRead, ct);
        events.EnsureSuccessStatusCode();
        var pushed = Channel.CreateUnbounded<Pushed>();
        _ = ReadEventsAsync(await events.Content.ReadAsStreamAsync(ct), pushed.Writer, ct);

        var first = await SendAsync(new SendMessageRequest(Html: $"<p>e2e html <b>{marker}</b></p>", ClientMessageId: clientMessageId), ct);
        Assert.Equal(clientMessageId, first.ClientMessageId);
        Assert.Matches("^[0-9]+$", first.MessageId ?? "");

        var firstPushed = await WaitForAsync(pushed.Reader, first.MessageId!, ct);
        Assert.Equal(clientMessageId, firstPushed.Message.ClientMessageId);
        Assert.Equal("RichText/Html", firstPushed.Message.Type);
        Assert.Contains($"<b>{marker}</b>", firstPushed.Message.Content);

        // 要等第一則進了 gateway（推回來了）才引用得到：replyToId 只收 gateway 收過的訊息
        var second = await SendAsync(new SendMessageRequest(Text: $"e2e reply {marker}", ReplyToId: first.MessageId), ct);
        var secondPushed = await WaitForAsync(pushed.Reader, second.MessageId!, ct);
        Assert.Equal("RichText/Html", secondPushed.Message.Type);
        Assert.Contains("http://schema.skype.com/Reply", secondPushed.Message.Content);
        Assert.Contains($"itemid=\"{first.MessageId}\"", secondPushed.Message.Content);
        Assert.Contains($"e2e reply {marker}", secondPushed.Message.Content);
        var quoted = Assert.Single(QuotedMessages(secondPushed.Raw));
        Assert.Equal(first.MessageId, quoted.GetProperty("messageId").ToString());
        Assert.Equal("Valid", quoted.GetProperty("validationResult").GetString());

        // 讀回來（chatsvc 讀路徑）也帶著 clientMessageId
        var page = await fx.Client.GetFromJsonAsync<MessagePage>($"/v1/chats/{fx.Chat}/messages?limit=20", ct);
        Assert.Contains(page!.Messages, m => m.Id == first.MessageId && m.ClientMessageId == clientMessageId);
    }

    private async Task<SendMessageResult> SendAsync(SendMessageRequest request, CancellationToken ct)
    {
        using var resp = await fx.Client.PostAsJsonAsync($"/v1/chats/{fx.Chat}/messages", request, ct);
        if (!resp.IsSuccessStatusCode)
            Assert.Fail($"Send failed with HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
        return (await resp.Content.ReadFromJsonAsync<SendMessageResult>(ct))!;
    }

    private static async Task<Pushed> WaitForAsync(ChannelReader<Pushed> reader, string messageId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PushTimeout);
        try
        {
            await foreach (var item in reader.ReadAllAsync(timeout.Token))
                if (item.Message.Id == messageId && !item.Message.Edited)
                    return item;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 落到下面的 Fail
        }
        Assert.Fail($"Message {messageId} did not come back over SSE within {PushTimeout.TotalSeconds}s");
        throw new UnreachableException();
    }

    private static async Task ReadEventsAsync(Stream stream, ChannelWriter<Pushed> writer, CancellationToken ct)
    {
        try
        {
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(ct))
            {
                if (item.EventType != EventKinds.Message)
                    continue;
                using var doc = JsonDocument.Parse(item.Data);
                var message = doc.RootElement.Deserialize<MessageDto>(JsonSerializerOptions.Web)!;
                var raw = doc.RootElement.TryGetProperty("raw", out var r) ? r.Clone() : default;
                await writer.WriteAsync(new Pushed(message, raw), ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // 測試結束、連線關掉
        }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <summary><c>properties.qtdMsgs</c>：推播裡是字串化的 JSON 陣列，讀回來是陣列，兩種都接。</summary>
    private static IReadOnlyList<JsonElement> QuotedMessages(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("properties", out var props) || !props.TryGetProperty("qtdMsgs", out var qtd))
            return [];
        using var doc = qtd.ValueKind == JsonValueKind.String ? JsonDocument.Parse(qtd.GetString()!) : null;
        var array = doc?.RootElement ?? qtd;
        return array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Select(e => e.Clone()).ToList() : [];
    }

    private readonly record struct Pushed(MessageDto Message, JsonElement Raw);
}
