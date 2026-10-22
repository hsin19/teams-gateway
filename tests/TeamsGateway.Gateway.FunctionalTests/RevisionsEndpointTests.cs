using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Data;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>
/// 編輯歷史端點整條走 HTTP：admin 建 key → key 授權 /v1 → 路由 → EF 查 → JSON 序列化。
/// 資料用真實程式碼路徑（MessageIngestor 的編輯 upsert）鋪，不碰 Teams。
/// </summary>
public sealed class RevisionsEndpointTests(GatewayAppFixture fx) : IClassFixture<GatewayAppFixture>
{
    private const string Conv = "19:func@thread.v2";

    private static MessageDto Msg(string id, long version, string content, bool edited = false) =>
        new(id, Conv, "8:me", "me", "Text", content, 1, DateTimeOffset.UtcNow, version, edited, false);

    [Fact]
    public async Task Admin_key_reads_edit_history_over_http()
    {
        var ct = TestContext.Current.CancellationToken;
        var messageId = "func-" + Guid.NewGuid().ToString("N");
        await SeedEditedMessageAsync(messageId, ct);

        var client = await fx.CreateApiClientAsync(ct);

        var doc = await client.GetFromJsonAsync<JsonElement>($"/v1/messages/{messageId}/revisions", ct);

        Assert.Equal("改過", doc.GetProperty("current").GetProperty("content").GetString());
        var revisions = doc.GetProperty("revisions");
        Assert.Equal(1, revisions.GetArrayLength());
        Assert.Equal("原文", revisions[0].GetProperty("content").GetString());
        Assert.Equal(100, revisions[0].GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Unknown_message_is_404()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await fx.CreateApiClientAsync(ct);

        var resp = await client.GetAsync("/v1/messages/does-not-exist/revisions", ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>插入 v100「原文」再用 v200「改過」upsert → 走真實編輯路徑產生一筆 revision。</summary>
    private async Task SeedEditedMessageAsync(string messageId, CancellationToken ct)
    {
        await using var scope = fx.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GatewayDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            if (!await db.Conversations.AnyAsync(c => c.Id == Conv, ct))
            {
                db.Conversations.Add(new Conversation { Id = Conv, Subscribed = true, UpdatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(ct);
            }
        }

        var ingestor = scope.ServiceProvider.GetRequiredService<MessageIngestor>();
        await ingestor.UpsertAsync(Msg(messageId, 100, "原文"), "{}", publish: false, ct);
        await ingestor.UpsertAsync(Msg(messageId, 200, "改過", edited: true), "{}", publish: false, ct);
    }
}
