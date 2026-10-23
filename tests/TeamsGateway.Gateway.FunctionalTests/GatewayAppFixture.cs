using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.FunctionalTests;

/// <summary>
/// 把整個 gateway 跑在測試宿主裡、打真 HTTP，但資料落臨時目錄（或 <see cref="TestDatabase"/> 的 Postgres）、關掉會連 Teams 的背景服務。
/// 測的是「我們的接線」（auth / 路由 / EF / 序列化）；會連 chatsvc 的讀發交給 E2E canary。
/// </summary>
public sealed class GatewayAppFixture : WebApplicationFactory<Program>
{
    public string AdminToken { get; } = "func-admin-" + Guid.NewGuid().ToString("N");

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "tgw-func-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 資料（SQLite + DataProtection key）落臨時目錄，不碰真的 .data
        builder.UseSetting("Gateway:DataDir", _dataDir);
        if (TestDatabase.PostgresConnectionString is { } server)
        {
            // GATEWAY_TEST_DB=postgres：共用的 Postgres server 上每個 fixture 一個資料庫（migration 會建），平行跑也互不干擾
            var database = new NpgsqlConnectionStringBuilder(server) { Database = "tgw_func_" + Guid.NewGuid().ToString("N") };
            builder.UseSetting("Gateway:Database:Provider", "Postgres");
            builder.UseSetting("ConnectionStrings:gateway", database.ConnectionString);
        }
        builder.UseSetting("Gateway:AdminToken", AdminToken);
        builder.UseSetting("Gateway:SseHeartbeatSeconds", "1"); // 心跳 / 撤銷檢查測試不用等 20 秒

        builder.ConfigureTestServices(services =>
        {
            // 這三個會在啟動時連 Teams（Trouter 連線、歷史回補、慢更新）——functional test 不要
            RemoveHostedService<TrouterBackgroundService>(services);
            RemoveHostedService<StartupBackfillService>(services);
            RemoveHostedService<ReconcileService>(services);
            RemoveHostedService<PollService>(services);

            // 模擬 AOT：API 的 JSON 拿掉 reflection，只剩 source-gen context。
            // 端點的 request / response 型別沒註冊進 context 就會直接失敗，不用等真的 publish AOT 才發現
            services.PostConfigure<JsonOptions>(options =>
            {
                foreach (var reflection in options.SerializerOptions.TypeInfoResolverChain.OfType<DefaultJsonTypeInfoResolver>().ToList())
                    options.SerializerOptions.TypeInfoResolverChain.Remove(reflection);
            });
        });
    }

    /// <summary>帶 admin token 的 client。</summary>
    public HttpClient CreateAdminClient()
    {
        var admin = CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Token", AdminToken);
        return admin;
    }

    /// <summary>走 admin 端點真的建一把 key（順便驗 admin happy path 回得到明文 apiKey）。</summary>
    public async Task<(Guid Id, string ApiKey)> CreateApiKeyAsync(CancellationToken ct)
    {
        var resp = await CreateAdminClient().PostAsJsonAsync("/admin/api-keys", new { name = "func" }, ct);
        resp.EnsureSuccessStatusCode();
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return (created.GetProperty("id").GetGuid(), created.GetProperty("apiKey").GetString()!);
    }

    /// <summary>帶一把新 key 的 client。</summary>
    public async Task<HttpClient> CreateApiClientAsync(CancellationToken ct) => ApiClient((await CreateApiKeyAsync(ct)).ApiKey);

    public HttpClient ApiClient(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private static void RemoveHostedService<T>(IServiceCollection services) where T : IHostedService
    {
        var descriptor = services.FirstOrDefault(s =>
            s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(T));
        if (descriptor is not null)
            services.Remove(descriptor);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_dataDir))
            try { Directory.Delete(_dataDir, recursive: true); } catch { /* 臨時目錄，清不掉就算了 */ }
    }
}
