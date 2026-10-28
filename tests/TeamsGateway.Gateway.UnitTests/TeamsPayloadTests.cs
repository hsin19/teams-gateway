using System.Net.Http.Json;
using System.Text.Json;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

/// <summary>
/// 送給 Teams 的 body 從匿名物件換成具名 record + source-gen：線上的 JSON 要逐字一樣（逆向來的協定，差一個字都可能壞）。
/// 每個 previous 都是原本的寫法、原本的序列化方式照抄。
/// </summary>
public class TeamsPayloadTests
{
    private const string Token = "skype-token";
    private const string Epid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string Surl = "https://pub-ent-sece-12-t.trouter.skype.com/v4/f/abc/";

    [Fact]
    public void Client_info()
    {
        var previous = JsonSerializer.Serialize(new { cv = "2026.39.01.1", ua = "TeamsCDL", hr = "", v = TeamsAuthConstants.ClientVersion });
        Assert.Equal(previous, TrouterClient.ClientInfo);
    }

    [Fact]
    public void Authenticate_frame()
    {
        var previous = JsonSerializer.Serialize(new
        {
            name = "user.authenticate",
            args = new object[] { new { headers = new Dictionary<string, string> { ["X-Skypetoken"] = Token } } },
        });
        Assert.Equal(previous, TrouterClient.AuthenticateJson(Token));
    }

    [Fact]
    public void Message_loss_ack_echoes_args()
    {
        using var doc = JsonDocument.Parse("""{"name":"trouter.message_loss","args":[{"droppedIndicators":[{"tag":"x","etag":"1"}]}]}""");
        var args = doc.RootElement.GetProperty("args");

        var previous = JsonSerializer.Serialize(new { name = "trouter.processed_message_loss", args });
        Assert.Equal(previous, TrouterClient.MessageLossAckJson(args));
    }

    [Fact]
    public void Push_ack()
    {
        var previous = JsonSerializer.Serialize(new
        {
            id = 42L,
            status = 200,
            headers = new Dictionary<string, string> { ["trouter-request"] = "req-1", ["trouter-client"] = "{\"cd\":1}" },
            body = "",
        });
        Assert.Equal(previous, TrouterClient.PushAckJson(42, "req-1"));
    }

    [Fact]
    public async Task Registrations()
    {
        var ct = TestContext.Current.CancellationToken;

        var main = await PreviousRegistrationAsync(new
        {
            appId = "TeamsCDLWebWorker", aesKey = "", languageId = "en-US", platform = "chrome",
            templateKey = "TeamsCDLWebWorker_2.7", platformUIVersion = TeamsAuthConstants.ClientVersion, productContext = "TFL",
        }, Epid, Surl, ct);
        var goldenGate = await PreviousRegistrationAsync(new
        {
            appId = "TeamsCDLWebWorker", aesKey = "", languageId = "", platform = "chrome",
            templateKey = "TeamsCDLWebWorker_2.3", platformUIVersion = TeamsAuthConstants.ClientVersion, productContext = "",
        }, $"{Epid}_GG", Surl.TrimEnd('/'), ct);

        Assert.Equal(main, await JsonContent.Create(TrouterClient.MainRegistration(Epid, Surl), TeamsJsonContext.Default.RegistrationRequest).ReadAsStringAsync(ct));
        Assert.Equal(goldenGate, await JsonContent.Create(TrouterClient.GoldenGateRegistration(Epid, Surl), TeamsJsonContext.Default.RegistrationRequest).ReadAsStringAsync(ct));
        Assert.Contains("\"trouter\":[", main); // 原本寫 TROUTER，JsonContent 的 Web camelCase 送出去是小寫
    }

    [Fact]
    public async Task Chatsvc_send_body()
    {
        var ct = TestContext.Current.CancellationToken;
        var previous = await JsonContent.Create(new
        {
            content = "hi <b>",
            messagetype = "Text",
            contenttype = "text",
            clientmessageid = "1234567890123456789",
            imdisplayname = "teams-gateway",
        }).ReadAsStringAsync(ct);

        var current = await JsonContent.Create(ChatsvcClient.SendBody(new OutgoingMessage("hi <b>"), "1234567890123456789", "teams-gateway"), TeamsJsonContext.Default.ChatsvcSendRequest)
            .ReadAsStringAsync(ct);

        Assert.Equal(previous, current);
    }

    [Fact]
    public async Task Chatsvc_send_body_for_html()
    {
        // HTML 改送 RichText/Html，其他欄位不變、沒有引用就不帶 properties
        var ct = TestContext.Current.CancellationToken;
        var body = await JsonContent.Create(ChatsvcClient.SendBody(new OutgoingMessage("<p><b>hi</b></p>", Html: true, ClientMessageId: "1729834567123456"), "1729834567123456", "teams-gateway"),
            TeamsJsonContext.Default.ChatsvcSendRequest).ReadAsStringAsync(ct);

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("<p><b>hi</b></p>", json.GetProperty("content").GetString());
        Assert.Equal("RichText/Html", json.GetProperty("messagetype").GetString());
        Assert.Equal("1729834567123456", json.GetProperty("clientmessageid").GetString());
        Assert.False(json.TryGetProperty("properties", out _));
    }

    [Fact]
    public void Chatsvc_send_body_for_quote_reply()
    {
        // 引用回覆：內容前面加 Reply blockquote，properties.qtdMsgs 是字串化的陣列（spike 驗證過兩個都要有）；純文字轉成跳脫過的段落
        var quote = new QuotedMessage("1793220565129", "8:live:alice", "Alice <A>", "earlier & older");

        var body = ChatsvcClient.SendBody(new OutgoingMessage("sure <ok>\nsee you"), "1729834567123456", "teams-gateway");
        var reply = ChatsvcClient.SendBody(new OutgoingMessage("sure <ok>\nsee you", Quote: quote), "1729834567123456", "teams-gateway");

        Assert.Equal("Text", body.MessageType);
        Assert.Equal("RichText/Html", reply.MessageType);
        Assert.Equal(
            """<blockquote itemscope="" itemtype="http://schema.skype.com/Reply" itemid="1793220565129">"""
            + """<strong itemprop="mri" itemid="8:live:alice">Alice &lt;A&gt;</strong><span itemprop="time" itemid="1793220565129"></span>"""
            + """<p itemprop="preview">earlier &amp; older</p></blockquote><p>sure &lt;ok&gt;<br>see you</p>""",
            reply.Content);
        Assert.Equal("""[{"messageId":1793220565129,"sender":"8:live:alice","time":1793220565129}]""", reply.Properties!.QtdMsgs);
    }

    [Fact]
    public void Login_cookies_round_trip_in_stored_format()
    {
        List<LoginCookie> cookies = [new("ESTSAUTHPERSISTENT", "v1", ".login.microsoftonline.com", "/"), new("MSPAuth", "v2", "login.live.com", null)];

        var previous = JsonSerializer.Serialize(cookies, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var current = JsonSerializer.Serialize(cookies, TeamsJsonContext.Default.ListLoginCookie);

        Assert.Equal(previous, current); // 已加密存著的 cookie 是這個格式，讀回來要相容
        Assert.Equal(cookies, JsonSerializer.Deserialize(previous, TeamsJsonContext.Default.ListLoginCookie));
    }

    // 原本 PostRegistrationAsync 的 body，照抄
    private static Task<string> PreviousRegistrationAsync(object clientDescription, string registrationId, string path, CancellationToken ct)
    {
        var body = new
        {
            clientDescription,
            registrationId,
            nodeId = "",
            transports = new { TROUTER = new[] { new { context = "", path, ttl = 3600 } } },
        };
        return JsonContent.Create(body).ReadAsStringAsync(ct);
    }
}
