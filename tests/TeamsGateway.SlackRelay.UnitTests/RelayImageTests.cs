using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeamsGateway.Contracts;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>
/// 圖片轉進 Slack：訊息裡的 <c>views/imgo</c> 是 Teams 的預覽縮圖（寬最多 800px、重壓成 JPEG），
/// relay 要先抓同一個物件的原圖，抓不到或格式 Slack 顯示不了才退回預覽圖。內容是合成的假資料。
/// </summary>
public class RelayImageTests
{
    private const string Conv = "19:x@thread.v2";
    private const string Preview = "https://us-api.asm.skype.com/v1/objects/0-a/views/imgo";
    private const string Original = "https://us-api.asm.skype.com/v1/objects/0-a/views/imgpsh_fullsize_anim";

    // 內嵌圖片：抓到原圖就直接用，不再抓預覽圖
    [Fact]
    public async Task Inline_image_relays_the_full_size_original()
    {
        var gateway = new MediaGateway { [Original] = (HttpStatusCode.OK, "image/png") };

        var images = await RelayAsync(gateway, $"<p><img src=\"{Preview}\"></p>");

        Assert.Equal([Original], gateway.Fetched);
        Assert.Equal(["F1"], images);
    }

    // 附件圖片給的預覽網址也是 views/imgo，一樣換成原圖
    [Fact]
    public async Task Attached_image_relays_the_full_size_original()
    {
        var gateway = new MediaGateway { [Original] = (HttpStatusCode.OK, "image/png") };

        var images = await RelayAsync(gateway, "<p>look</p>", [new AttachmentDto("photo.png", "png", Preview, "https://1drv.ms/i/c/x/abc")]);

        Assert.Equal([Original], gateway.Fetched);
        Assert.Equal(["F1"], images);
    }

    // 原圖抓不到（gateway 回 502）就退回預覽圖，圖照樣轉過去
    [Fact]
    public async Task Falls_back_to_the_preview_when_the_original_fails()
    {
        var gateway = new MediaGateway
        {
            [Original] = (HttpStatusCode.BadGateway, "application/problem+json"),
            [Preview] = (HttpStatusCode.OK, "image/jpeg"),
        };

        var images = await RelayAsync(gateway, $"<p><img src=\"{Preview}\"></p>");

        Assert.Equal([Original, Preview], gateway.Fetched);
        Assert.Equal(["F1"], images);
    }

    // 原圖是 Slack image block 顯示不了的格式（webp），退回一律是 JPEG 的預覽圖
    [Fact]
    public async Task Falls_back_to_the_preview_when_slack_cannot_show_the_original()
    {
        var gateway = new MediaGateway
        {
            [Original] = (HttpStatusCode.OK, "image/webp"),
            [Preview] = (HttpStatusCode.OK, "image/jpeg"),
        };

        var images = await RelayAsync(gateway, $"<p><img src=\"{Preview}\"></p>");

        Assert.Equal([Original, Preview], gateway.Fetched);
        Assert.Equal(["F1"], images);
    }

    // 不是 views/imgo 的網址（已經是原圖等）照原樣抓
    [Fact]
    public async Task Other_media_urls_are_fetched_as_is()
    {
        var gateway = new MediaGateway { [Original] = (HttpStatusCode.OK, "image/gif") };

        var images = await RelayAsync(gateway, $"<p><img src=\"{Original}\"></p>");

        Assert.Equal([Original], gateway.Fetched);
        Assert.Equal(["F1"], images);
    }

    /// <summary>轉一則 Bob 的訊息，回傳貼到 Slack 那則訊息裡 image block 引用的 Slack file id。</summary>
    private static async Task<IReadOnlyList<string>> RelayAsync(MediaGateway gateway, string content, IReadOnlyList<AttachmentDto>? attachments = null)
    {
        var slack = new RecordingSlack();
        slack.Responses["files.getUploadURLExternal"] = """{"ok":true,"upload_url":"https://files.slack.test/upload/F1","file_id":"F1"}""";
        var links = new MessageLinks();
        var worker = new RelayWorker(
            new SseEventSource(new GatewayEventStream(new HttpClient(), NullLogger<GatewayEventStream>.Instance), new MessageLinks(), NullLogger<SseEventSource>.Instance),
            RelayFakes.Slack(slack),
            new GatewayMediaClient(new HttpClient(gateway) { BaseAddress = new Uri("http://gateway.test") }, NullLogger<GatewayMediaClient>.Instance),
            links,
            RelayFakes.Sends(slack, links),
            RelayFakes.Bot(slack),
            Options.Create(new RelayOptions()),
            NullLogger<RelayWorker>.Instance);
        var message = new MessageDto("1792640099000", Conv, "8:bob", "Bob", "RichText/Html", content, 2, DateTimeOffset.UtcNow, 1792640099000,
            Attachments: attachments);

        await worker.RelayWithRetryAsync(message, new Dictionary<string, string> { [Conv] = "C1" }, null, TestContext.Current.CancellationToken);

        var post = slack.Requests.Single(r => r.Method == "chat.postMessage").Body;
        return post.TryGetProperty("blocks", out var blocks)
            ? blocks.EnumerateArray()
                .Where(b => b.GetProperty("type").GetString() == "image")
                .Select(b => b.GetProperty("slack_file").GetProperty("id").GetString()!)
                .ToList()
            : [];
    }

    /// <summary>假的 gateway <c>/v1/media</c>：記下依序抓了哪些網址，照表回狀態碼與 Content-Type；表上沒有的回 502。</summary>
    private sealed class MediaGateway : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string ContentType)> _media = [];

        public List<string> Fetched { get; } = [];

        public (HttpStatusCode Status, string ContentType) this[string url]
        {
            get => _media[url];
            set => _media[url] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["url"]!;
            Fetched.Add(url);
            var (status, contentType) = _media.TryGetValue(url, out var canned) ? canned : (HttpStatusCode.BadGateway, "application/problem+json");
            var body = new ByteArrayContent([1, 2, 3]);
            body.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return Task.FromResult(new HttpResponseMessage(status) { Content = body });
        }
    }
}
