using Microsoft.AspNetCore.DataProtection;

namespace TeamsGateway.Gateway.Teams;

public static class TeamsExtensions
{
    public static IHostApplicationBuilder AddTeams(this IHostApplicationBuilder builder)
    {
        var dataDir = builder.Configuration["Gateway:DataDir"]
            ?? Path.Combine(builder.Environment.ContentRootPath, ".data");

        // DataProtection 的 key 存檔，重啟後才解得開存下來的 cookie（key 掉了就得重新匯入 cookie）
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
            .SetApplicationName("teams-gateway");

        builder.Services.AddSingleton<SecureStore>();
        builder.Services.AddSingleton<SkypeTokenProvider>();
        builder.Services.AddSingleton<IPeopleDirectory, PeopleDirectory>();
        builder.Services.AddHttpClient(nameof(PeopleDirectory));
        builder.Services.AddHttpClient<ChatsvcClient>(client =>
            client.BaseAddress = new Uri("https://teams.live.com/api/chatsvc/consumer/v1/users/ME/"));
        builder.Services.AddHttpClient<MediaClient>();
        return builder;
    }
}
