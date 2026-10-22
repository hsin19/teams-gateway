using System.Net;

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
}
