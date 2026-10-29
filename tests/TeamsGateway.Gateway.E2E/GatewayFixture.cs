using System.Net.Http.Headers;

namespace TeamsGateway.Gateway.E2E;

/// <summary>
/// 指向一台實際在跑的 gateway（預設本機，CI 指到部署目標）。
/// 設定全走環境變數，所以同一份測試能打 localhost 或線上。
/// 沒有 <c>GATEWAY_API_KEY</c> 就代表沒配置 live 目標，canary 一律 skip。
/// </summary>
public sealed class GatewayFixture : IDisposable
{
    public string BaseUrl { get; } = Environment.GetEnvironmentVariable("GATEWAY_BASE_URL") ?? "http://localhost:5199";
    public string? ApiKey { get; } = Environment.GetEnvironmentVariable("GATEWAY_API_KEY");

    /// <summary>發話 round-trip 用的對話；預設是只有自己的自測群，不會吵到真群。</summary>
    public string Chat { get; } =
        Environment.GetEnvironmentVariable("TEAMS_E2E_CHAT") ?? "19:00000000000000000000000000000000@thread.v2";

    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);

    public HttpClient Client { get; }

    private readonly Lazy<Task<Version?>> _version;

    public GatewayFixture()
    {
        Client = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrWhiteSpace(ApiKey))
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        _version = new(FetchVersionAsync);
    }

    /// <summary>
    /// 目標 gateway 比 <paramref name="minimum"/> 舊就 skip：功能合併了、部署端還沒換版時，每天的 canary 不會因為舊版沒有這個功能而紅。
    /// 本機沒帶版本時是 <c>1.0.0</c>，一律會跑；讀不到版本（連不上、看不懂）也照跑，讓真正的錯誤露出來。
    /// </summary>
    public async Task SkipUnlessVersionAtLeastAsync(string minimum)
    {
        var target = await _version.Value;
        Assert.SkipWhen(target is { } v && v < Version.Parse(minimum), $"Gateway {target} is older than {minimum}; skipping");
    }

    /// <summary><c>/version</c> 的 <c>0.6.1+9028dee</c> → 0.6.1；看不懂回 null。</summary>
    internal static Version? ParseVersion(string text) =>
        Version.TryParse(text.Split('+', '-')[0].Trim(), out var version) ? version : null;

    private async Task<Version?> FetchVersionAsync()
    {
        try
        {
            return ParseVersion(await Client.GetStringAsync("/version"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public void Dispose() => Client.Dispose();
}
