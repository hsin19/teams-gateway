using TeamsGateway.Service.Apis;
using TeamsGateway.Service.Auth;
using TeamsGateway.Service.Data;
using TeamsGateway.Service.Events;
using TeamsGateway.Service.Teams;

namespace TeamsGateway.Service;

/// <summary>把所有服務註冊與端點對應收在一處，讓 Program.cs 保持極薄（eShop 的 AddApplicationServices pattern）。</summary>
public static class Extensions
{
    public static void AddApplicationServices(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<GatewayOptions>().BindConfiguration("Gateway");

        builder.AddGatewayDatabase();
        builder.AddTeams();

        builder.Services.AddSingleton<ApiKeyService>();

        // 即時推播：Trouter 背景連線 → 行程內匯流排 → SSE
        builder.Services.AddSingleton<GatewayEventBus>();
        builder.Services.AddSingleton<TrouterClient>();
        builder.Services.AddHostedService<TrouterBackgroundService>();
    }

    public static async Task UseApplicationAsync(this WebApplication app)
    {
        await app.Services.MigrateGatewayDatabaseAsync();

        app.MapDefaultEndpoints();
        app.MapAdminApi();
        app.MapChatsApi();
        app.MapEventsApi();
        app.MapGet("/", () => "teams-gateway");
    }
}
