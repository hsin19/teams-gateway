using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>SSE 整條走 HTTP：帶 Last-Event-ID 重連先補收、再接即時。事件用真的 ingest 路徑產生，不碰 Teams。</summary>
public sealed class EventsEndpointTests(GatewayAppFixture fx) : IClassFixture<GatewayAppFixture>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Reconnect_replays_after_last_event_id_then_streams_live()
    {
        var ct = TestContext.Current.CancellationToken;
        var conv = $"19:sse-{Guid.NewGuid():N}@thread.v2";
        var prefix = Guid.NewGuid().ToString("N");

        await using var scope = fx.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GatewayDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            db.Conversations.Add(new Conversation { Id = conv, Subscribed = true, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
        }
        var ingestor = scope.ServiceProvider.GetRequiredService<MessageIngestor>();
        MessageDto Msg(string id, long version, string content) =>
            new($"{prefix}-{id}", conv, "8:me", "me", "Text", content, 1, DateTimeOffset.UtcNow, version);

        await ingestor.UpsertAsync(Msg("a", 1, "A"), "{}", publish: true, ct);
        await ingestor.UpsertAsync(Msg("b", 1, "B"), "{}", publish: true, ct);
        await ingestor.UpsertAsync(Msg("a", 2, "A 改過"), "{}", publish: true, ct);
        long firstSeq;
        await using (var db = await factory.CreateDbContextAsync(ct))
            firstSeq = await db.PublishedEvents.Where(e => e.ConversationId == conv).MinAsync(e => e.Seq, ct);

        var client = await fx.CreateApiClientAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/v1/events?chats={Uri.EscapeDataString(conv)}");
        req.Headers.Add("Last-Event-ID", firstSeq.ToString());
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        resp.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(timeout.Token));

        // 補收：第一個事件之後的 B、A 的編輯，照序號
        var b = await NextAsync(reader, timeout.Token);
        var edit = await NextAsync(reader, timeout.Token);
        Assert.Equal(($"{prefix}-b", firstSeq + 1), (b.Message.Id, b.Seq));
        Assert.Equal(($"{prefix}-a", firstSeq + 2, "A 改過"), (edit.Message.Id, edit.Seq, edit.Message.Content));

        // 接即時
        await ingestor.UpsertAsync(Msg("c", 1, "C"), "{}", publish: true, ct);
        var live = await NextAsync(reader, timeout.Token);
        Assert.Equal(($"{prefix}-c", firstSeq + 3), (live.Message.Id, live.Seq));
    }

    [Fact]
    public async Task Idle_stream_sends_heartbeats()
    {
        var ct = TestContext.Current.CancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var client = await fx.CreateApiClientAsync(ct);

        using var resp = await client.GetAsync($"/v1/events?chats=19:idle-{Guid.NewGuid():N}@thread.v2", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(timeout.Token));

        string? line;
        do
            line = await reader.ReadLineAsync(timeout.Token);
        while (line is not null && line != ": ping");
        Assert.Equal(": ping", line);
    }

    [Fact]
    public async Task Revoking_the_key_ends_the_stream()
    {
        // 長連線只在連上時驗過 key：撤銷後要在下一次心跳斷掉
        var ct = TestContext.Current.CancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var (keyId, apiKey) = await fx.CreateApiKeyAsync(ct);

        using var resp = await fx.ApiClient(apiKey).GetAsync("/v1/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(timeout.Token));
        Assert.Equal(": ok", await reader.ReadLineAsync(timeout.Token));

        (await fx.CreateAdminClient().DeleteAsync($"/admin/api-keys/{keyId}", timeout.Token)).EnsureSuccessStatusCode();

        while (await reader.ReadLineAsync(timeout.Token) is not null)
        {
        }
    }

    private static async Task<(long Seq, MessageDto Message)> NextAsync(StreamReader reader, CancellationToken ct)
    {
        long? seq = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
                seq = long.Parse(line["id: ".Length..]);
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                return (seq!.Value, JsonSerializer.Deserialize<MessageDto>(line["data: ".Length..], Web)!);
        }
        throw new InvalidOperationException("SSE 串流提早結束");
    }
}
