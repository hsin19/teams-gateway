using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

public class TrouterReceiveTests
{
    [Fact]
    public async Task Multibyte_character_split_across_fragments_survives()
    {
        // 「中」是 3 bytes：在它中間切段，各段各自解碼會變成 U+FFFD
        const string text = """3:::{"body":"{\"content\":\"中文訊息\"}"}""";
        var bytes = Encoding.UTF8.GetBytes(text);
        var split = Encoding.UTF8.GetByteCount(text[..text.IndexOf('中')]) + 1;
        var ws = FakeWebSocket.WithFragments([bytes[..split], bytes[split..]]);

        var received = await TrouterClient.ReceiveTextAsync(ws, new byte[64], new ArrayBufferWriter<byte>(), TestContext.Current.CancellationToken);

        Assert.Equal(text, received);
    }

    [Fact]
    public async Task Close_returns_null()
    {
        var ws = FakeWebSocket.WithMessages();
        Assert.Null(await TrouterClient.ReceiveTextAsync(ws, new byte[64], new ArrayBufferWriter<byte>(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Bad_frames_and_failing_handlers_do_not_drop_the_connection()
    {
        // 壞掉的 5: frame、處理會丟例外的推播，都要略過（推播照樣 ack），後面的推播照收
        using var db = new TestDb();
        var trouter = new TrouterClient(
            new SkypeTokenProvider(new SecureStore(db, new EphemeralDataProtectionProvider()), NullLogger<SkypeTokenProvider>.Instance),
            new NoHttp(),
            NullLogger<TrouterClient>.Instance);
        var ws = FakeWebSocket.WithMessages(
            "5:1::not json",
            Push(1, "boom"),
            Push(2, "ok"));
        var handled = new List<string?>();

        await trouter.ReceiveLoopAsync(ws, "token", "epid", (incoming, _) =>
        {
            if (incoming.Message.Content == "boom")
                throw new InvalidOperationException("DB 一時寫不進去");
            handled.Add(incoming.Message.Content);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["ok"], handled);
        Assert.Equal(2, ws.Sent.Count(s => s.StartsWith("3:::", StringComparison.Ordinal))); // 兩則推播都有 ack
    }

    private static string Push(long id, string content)
    {
        var body = JsonSerializer.Serialize(new
        {
            resourceType = "NewMessage",
            resource = new { id = $"17926400000{id:00}", to = "19:x@thread.v2", messagetype = "Text", content },
        });
        return "3:::" + JsonSerializer.Serialize(new { id, headers = new Dictionary<string, string> { ["trouter-request"] = $"r{id}" }, body });
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}
