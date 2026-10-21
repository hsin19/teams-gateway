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

    public GatewayFixture()
    {
        Client = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrWhiteSpace(ApiKey))
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    public void Dispose() => Client.Dispose();
}
