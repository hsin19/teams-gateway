using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>
/// 送出結果不明時到對話裡找那則（ADR 0012）：只翻到那則 Slack 訊息寫下之前；確定沒有才回 null，
/// 查不完（游標重複、翻到上限）要丟例外，呼叫端不能當成確定沒送而重送。
/// </summary>
public class GatewaySendClientTests
{
    // clientMessageId = Slack ts 1729834567.123456；送出不可能早於這個時間減掉時鐘誤差
    private const string ClientMessageId = "1729834567123456";
    private const long Recent = 1729834600000;  // Slack 訊息之後
    private const long Older = 1729000000000;   // Slack 訊息之前好幾天

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 第一頁沒有：跟著 nextCursor 翻下一頁找到
    [Fact]
    public async Task Find_sent_follows_cursors_until_the_send_is_found()
    {
        var gateway = new PagedGateway(
            Page([Message("m1", Recent)], "https://teams.microsoft.com/page?token=a%2Bb"),
            Page([Message("teams-id", Recent, ClientMessageId)]));

        Assert.Equal("teams-id", await Client(gateway).FindSentAsync("chat", ClientMessageId, Ct));
        Assert.Equal(2, gateway.Requests.Count);
        Assert.Contains("limit=200", gateway.Requests[0]);
        Assert.Contains("cursor=https%3A%2F%2Fteams.microsoft.com%2Fpage%3Ftoken%3Da%252Bb", gateway.Requests[1]);
    }

    // 翻到比 Slack 訊息還舊的就停：再往前不可能有，長年的對話不用整段翻完
    [Fact]
    public async Task Find_sent_stops_once_a_page_reaches_before_the_slack_message()
    {
        var gateway = new PagedGateway(
            Page([Message("m1", Recent), Message("m0", Older)], "c1"),
            Page([Message("teams-id", Older, ClientMessageId)]));

        Assert.Null(await Client(gateway).FindSentAsync("chat", ClientMessageId, Ct));
        Assert.Single(gateway.Requests);
    }

    // 空頁就是對話開頭（跟 gateway 自己翻頁的規則一樣），就算還帶著游標
    [Fact]
    public async Task Find_sent_stops_on_an_empty_page()
    {
        var gateway = new PagedGateway(Page([], "c1"), Page([Message("teams-id", Recent, ClientMessageId)]));

        Assert.Null(await Client(gateway).FindSentAsync("chat", ClientMessageId, Ct));
        Assert.Single(gateway.Requests);
    }

    // 游標重複：查不完，不能說確定沒有
    [Fact]
    public async Task Find_sent_rejects_a_repeated_cursor_instead_of_claiming_the_send_is_missing()
    {
        var gateway = new PagedGateway(Page([Message("m1", Recent)], "same"), Page([Message("m2", Recent)], "same"));

        await Assert.ThrowsAsync<InvalidDataException>(() => Client(gateway).FindSentAsync("chat", ClientMessageId, Ct));
    }

    // 翻到上限還沒到 Slack 訊息寫下的時間：一樣是查不完
    [Fact]
    public async Task Find_sent_gives_up_at_the_page_cap_without_claiming_the_send_is_missing()
    {
        var gateway = new PagedGateway(index => Page([Message($"m{index}", Recent)], $"c{index}"));

        await Assert.ThrowsAsync<InvalidDataException>(() => Client(gateway).FindSentAsync("chat", ClientMessageId, Ct));
        Assert.Equal(GatewaySendClient.MaxSearchPages, gateway.Requests.Count);
    }

    private static GatewaySendClient Client(PagedGateway gateway) =>
        new(new HttpClient(gateway) { BaseAddress = new Uri("http://gateway.test") }, NullLogger<GatewaySendClient>.Instance);

    private static string Message(string id, long version, string? clientMessageId = null) =>
        $$"""{"id":"{{id}}","conversationId":"chat","sequenceId":1,"time":"2026-10-28T00:00:00Z","version":{{version}}{{(clientMessageId is null ? "" : $",\"clientMessageId\":\"{clientMessageId}\"")}}}""";

    private static string Page(string[] messages, string? cursor = null) =>
        $$"""{"messages":[{{string.Join(",", messages)}}],"nextCursor":{{(cursor is null ? "null" : $"\"{cursor}\"")}}}""";

    /// <summary>依請求順序回第 n 頁；頁數不夠時重複最後一頁。</summary>
    private sealed class PagedGateway(Func<int, string> page) : HttpMessageHandler
    {
        public PagedGateway(params string[] pages) : this(i => pages[Math.Min(i, pages.Length - 1)])
        {
        }

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(page(Requests.Count - 1), Encoding.UTF8, "application/json"),
            });
        }
    }
}
