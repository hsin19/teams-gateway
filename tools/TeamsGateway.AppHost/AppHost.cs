using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// 只在本機開發用：起 gateway + slack-relay 並接上 Aspire dashboard（log / trace / metrics）。
// 正式部署用 CI 出的 image（見根目錄 README〈部署〉），不部 AppHost。

// 本機的 secret 與設定放這個專案的 appsettings.Local.json（gitignored），key 就是服務吃的扁平環境變數名稱。
// 下面 WithLocalEnv / WithLocalEnvPrefix 帶的，都是正式環境也要設的環境變數。
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);

// 不指定的話 DataDir 預設是 content root（gateway 專案目錄）下的 .data，跟 repo 根目錄那份分家
var dataDir = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", ".data"));

var gateway = builder.AddProject<Projects.TeamsGateway_Gateway>("gateway")
    .WithLocalEnv("Gateway__AdminToken")
    .WithLocalEnv("Gateway__Database__Provider")
    .WithEnvironment("Gateway__DataDir", dataDir)
    .WithHttpHealthCheck("/health", endpointName: "http");

// DB 預設 SQLite（DataDir 裡的 gateway.db）；appsettings.Local.json 設 Gateway__Database__Provider=Postgres 就改起一個
// Postgres container（要有 Docker），連線字串由 ConnectionStrings__gateway 帶進去。資料放 named volume，AppHost 重起還在
if (string.Equals(builder.Configuration["Gateway__Database__Provider"], "Postgres", StringComparison.OrdinalIgnoreCase))
{
    var database = builder.AddPostgres("postgres").WithDataVolume().AddDatabase("gateway-db", databaseName: "gateway");
    gateway.WithReference(database, connectionName: "gateway").WaitFor(database);
}

// Routes 是 list，攤平成 Relay__Slack__Routes__<i>__Chat / __<i>__Channel
builder.AddProject<Projects.TeamsGateway_SlackRelay>("slack-relay")
    .WithEnvironment("Relay__Gateway__BaseUrl", gateway.GetEndpoint("http"))
    .WithLocalEnv("Relay__Gateway__ApiKey")
    .WithLocalEnv("Relay__Slack__BotToken")
    .WithLocalEnv("Relay__Slack__DefaultChannel")
    .WithLocalEnvPrefix("Relay__Slack__Routes__")
    .WaitFor(gateway);

builder.Build().Run();

internal static class LocalEnvExtensions
{
    /// <summary>
    /// 把 appsettings.Local.json 裡同名的 key 原樣當環境變數帶給服務。沒設就不帶、由服務自己處理缺值
    /// （gateway 沒 admin token 會關掉 /admin，relay 沒 token 會記 error 不啟動），第一次 bootstrap 前還沒有 API key 也起得來。
    /// </summary>
    public static IResourceBuilder<T> WithLocalEnv<T>(this IResourceBuilder<T> resource, string name)
        where T : IResourceWithEnvironment
    {
        var value = resource.ApplicationBuilder.Configuration[name];
        return value is null ? resource : resource.WithEnvironment(name, value);
    }

    /// <summary>同 <see cref="WithLocalEnv{T}"/>，帶所有以 <paramref name="prefix"/> 開頭的 key（list 攤平後的 <c>__0__</c>、<c>__1__</c>…）。</summary>
    public static IResourceBuilder<T> WithLocalEnvPrefix<T>(this IResourceBuilder<T> resource, string prefix)
        where T : IResourceWithEnvironment
    {
        foreach (var (key, value) in resource.ApplicationBuilder.Configuration.AsEnumerable())
        {
            if (value is not null && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                resource.WithEnvironment(key, value);
            }
        }
        return resource;
    }
}
