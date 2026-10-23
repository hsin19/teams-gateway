using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TeamsGateway.Gateway.UnitTests;

/// <summary>ServiceDefaults 對所有 HttpClient 套的 resilience：GET 重試，POST 不重試（避免重複發話 / 貼文）。</summary>
public class ResilienceDefaultsTests
{
    [Fact]
    public async Task Post_is_not_retried_on_server_error()
    {
        var (client, handler) = Build();

        using var resp = await client.PostAsync("http://example.test/send", new StringContent("hi"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Get_is_still_retried()
    {
        var (client, handler) = Build();

        using var resp = await client.GetAsync("http://example.test/read", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // 第一次 503、重試後 200
        Assert.Equal(2, handler.Calls);
    }

    private static (HttpClient Client, FailOnce Handler) Build()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        var handler = new FailOnce();
        builder.Services.AddHttpClient("test").ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = builder.Services.BuildServiceProvider();
        return (provider.GetRequiredService<IHttpClientFactory>().CreateClient("test"), handler);
    }

    /// <summary>第一次回 503，之後回 200。</summary>
    private sealed class FailOnce : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(++Calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
    }
}
