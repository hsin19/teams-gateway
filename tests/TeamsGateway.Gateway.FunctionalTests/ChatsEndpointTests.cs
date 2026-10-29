using System.Net;
using System.Net.Http.Json;
using TeamsGateway.Contracts;

namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>/v1/chats 的輸入檢查：在打 chatsvc 之前就擋掉，不碰 Teams。</summary>
public sealed class ChatsEndpointTests(GatewayAppFixture fx) : IClassFixture<GatewayAppFixture>
{
    [Theory]
    [InlineData("https://attacker.example/api/chatsvc/consumer/v1/users/ME/conversations/19:abc@thread.v2/messages")]
    [InlineData("https://teams.live.com/api/chatsvc/consumer/v1/users/ME/conversations/19:other@thread.v2/messages")]
    public async Task Foreign_cursor_is_rejected_before_calling_teams(string cursor)
    {
        // 游標會帶著 skypetoken 原樣打出去：別的主機 / 別的對話一律 400
        var ct = TestContext.Current.CancellationToken;
        var client = await fx.CreateApiClientAsync(ct);

        var resp = await client.GetAsync(
            $"/v1/chats/{Uri.EscapeDataString("19:abc@thread.v2")}/messages?cursor={Uri.EscapeDataString(cursor)}", ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData(null, null, null, null)]                                   // text / html 都沒給
    [InlineData("hi", "<p>hi</p>", null, null)]                            // 兩個都給
    [InlineData("hi", null, "abc", null)]                                  // clientMessageId 不是數字
    [InlineData("hi", null, "12345678901234567890", null)]                 // 超過 19 碼
    [InlineData("hi", null, null, "1792640000000")]                        // 引用 gateway 沒收過的訊息
    public async Task Invalid_send_is_rejected_before_calling_teams(string? text, string? html, string? clientMessageId, string? replyToId)
    {
        // 發話不能撤回：參數不對一律 400，不打 Teams
        var ct = TestContext.Current.CancellationToken;
        var client = await fx.CreateApiClientAsync(ct);

        var resp = await client.PostAsJsonAsync($"/v1/chats/{Uri.EscapeDataString("19:abc@thread.v2")}/messages", new SendMessageRequest(text, html, clientMessageId, replyToId), ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData(null, null, "1792640000000", null)]                        // text / html 都沒給
    [InlineData("hi", "<p>hi</p>", "1792640000000", null)]                 // 兩個都給
    [InlineData("hi", null, "abc", null)]                                  // messageId 不是 Teams 的訊息 id
    [InlineData("hi", null, "1792640000000", "1792640000001")]             // 引用 gateway 沒收過的訊息
    public async Task Invalid_edit_is_rejected_before_calling_teams(string? text, string? html, string messageId, string? replyToId)
    {
        // 編輯是整則換掉：參數不對一律 400，不打 Teams
        var ct = TestContext.Current.CancellationToken;
        var client = await fx.CreateApiClientAsync(ct);

        var resp = await client.PutAsJsonAsync(
            $"/v1/chats/{Uri.EscapeDataString("19:abc@thread.v2")}/messages/{Uri.EscapeDataString(messageId)}", new EditMessageRequest(text, html, replyToId), ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_with_a_non_teams_message_id_is_rejected_before_calling_teams()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await fx.CreateApiClientAsync(ct);

        var resp = await client.DeleteAsync($"/v1/chats/{Uri.EscapeDataString("19:abc@thread.v2")}/messages/abc", ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
