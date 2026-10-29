using System.Net.Http.Headers;

namespace TeamsGateway.Gateway.E2E;

/// <summary>
/// 指向一台實際在跑的 gateway（預設本機，CI 指到部署目標）。
/// 設定全走環境變數，所以同一份測試能打 localhost 或線上。
/// 沒有 <c>GATEWAY_API_KEY</c> 就代表沒配置 live 目標，測試由 <see cref="LiveFactAttribute"/> 一律 skip。
/// </summary>
public sealed class GatewayFixture : IDisposable
{
    public string BaseUrl => GatewayTarget.BaseUrl;
    public string? ApiKey => GatewayTarget.ApiKey;

    /// <summary>發話 round-trip 用的對話；預設是只有自己的自測群，不會吵到真群。</summary>
    public string Chat { get; } =
        Environment.GetEnvironmentVariable("TEAMS_E2E_CHAT") ?? "19:00000000000000000000000000000000@thread.v2";

    public HttpClient Client { get; }

    public GatewayFixture()
    {
        Client = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrWhiteSpace(ApiKey))
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    public void Dispose() => Client.Dispose();
}

/// <summary>打哪台 gateway（環境變數），fixture 與 <see cref="LiveFactAttribute"/>、<see cref="MinGatewayVersionAttribute"/> 共用（xunit 要用反射讀 <c>Configured</c>，所以是 public）。</summary>
public static class GatewayTarget
{
    public static string BaseUrl { get; } = Environment.GetEnvironmentVariable("GATEWAY_BASE_URL") ?? "http://localhost:5199";
    public static string? ApiKey { get; } = Environment.GetEnvironmentVariable("GATEWAY_API_KEY");
    public static bool Configured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>目標的版本（<c>/version</c>，不用 API key），整個測試行程只查一次；讀不到是 null。</summary>
    public static Lazy<Task<Version?>> Version { get; } = new(FetchVersionAsync);

    /// <summary><c>/version</c> 的 <c>0.6.1+9028dee</c> → 0.6.1；看不懂回 null。</summary>
    internal static Version? ParseVersion(string text) =>
        System.Version.TryParse(text.Split('+', '-')[0].Trim(), out var version) ? version : null;

    private static async Task<Version?> FetchVersionAsync()
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
            return ParseVersion(await http.GetStringAsync("/version"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
