using Microsoft.Extensions.Options;

namespace TeamsGateway.SlackRelay;

public static class Extensions
{
    public static void AddRelayServices(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<RelayOptions>().BindConfiguration("Relay");

        // SSE 是長連線，刻意不走 IHttpClientFactory——ServiceDefaults 對 factory client 預設套的
        // resilience handler 有 ~30s timeout 會掐斷串流。單一 app-lifetime 的 HttpClient 正合適。
        builder.Services.AddSingleton(sp =>
        {
            var gateway = sp.GetRequiredService<IOptions<RelayOptions>>().Value.Gateway;
            var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) })
            {
                BaseAddress = new Uri(gateway.BaseUrl),
                Timeout = Timeout.InfiniteTimeSpan,
            };
            if (!string.IsNullOrWhiteSpace(gateway.ApiKey))
                http.DefaultRequestHeaders.Authorization = new("Bearer", gateway.ApiKey);
            return new GatewayEventStream(http, sp.GetRequiredService<ILogger<GatewayEventStream>>());
        });

        // Slack 是短請求，走 factory：resilience 的 transient retry 反而有用
        builder.Services.AddHttpClient<SlackClient>((sp, http) =>
        {
            var slack = sp.GetRequiredService<IOptions<RelayOptions>>().Value.Slack;
            http.BaseAddress = new Uri("https://slack.com/api/");
            if (!string.IsNullOrWhiteSpace(slack.BotToken))
                http.DefaultRequestHeaders.Authorization = new("Bearer", slack.BotToken);
        });

        builder.Services.AddHostedService<RelayWorker>();
    }
}
