using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace TeamsGateway.Gateway.Auth;

/// <summary>
/// 認證與授權（ADR 0011）：每個 scheme 只認自己的 header、產生「是誰」；端點只要求角色（<see cref="GatewayRoles"/>）。
/// </summary>
public static class AuthExtensions
{
    public static void AddGatewayAuth(this IHostApplicationBuilder builder)
    {
        var access = builder.Configuration.GetSection("Gateway:CloudflareAccess").Get<CloudflareAccessSettings>();
        CloudflareAccess.EnsureValid(access);

        var cloudflareAccess = !string.IsNullOrWhiteSpace(access?.Issuer);

        // 一個請求一種身分：看帶了哪種 header 交給對應的 scheme（同時帶兩種的，照這個順序只看前面那個）
        var authentication = builder.Services.AddAuthentication(AuthSchemes.Default)
            .AddPolicyScheme(AuthSchemes.Default, null, options => options.ForwardDefaultSelector = context =>
                cloudflareAccess && context.Request.Headers.ContainsKey(CloudflareAccess.HeaderName) ? AuthSchemes.CloudflareAccess
                : context.Request.Headers.ContainsKey(AdminTokenAuthenticationHandler.HeaderName) ? AuthSchemes.AdminToken
                : AuthSchemes.ApiKey)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthSchemes.ApiKey, null)
            .AddScheme<AuthenticationSchemeOptions, AdminTokenAuthenticationHandler>(AuthSchemes.AdminToken, null);
        if (cloudflareAccess)
        {
            authentication.AddJwtBearer(AuthSchemes.CloudflareAccess);
            builder.Services.AddOptions<JwtBearerOptions>(AuthSchemes.CloudflareAccess)
                .Configure<IHttpClientFactory>((options, http) => CloudflareAccess.Configure(options, access!, http));
            builder.Services.AddHttpClient(CloudflareAccess.HttpClientName);
        }

        builder.Services.AddSingleton<IRoleDirectory, ConfiguredRoleDirectory>();
        builder.Services.AddScoped<CurrentUser>();
        builder.Services.AddScoped<IAuthorizationHandler, RoleRequirementHandler>();

        // policy 只要求角色。沒有身分 401、有身分但沒有那個角色 403
        var authorization = builder.Services.AddAuthorizationBuilder();
        foreach (var role in new[] { GatewayRoles.Admin, GatewayRoles.Api })
            authorization.AddPolicy(role, policy => policy.AddRequirements(new RoleRequirement(role)));
    }

    /// <summary>認證 → 把這個請求的使用者填進 <see cref="CurrentUser"/> → 授權。</summary>
    public static void UseGatewayAuth(this WebApplication app)
    {
        app.UseAuthentication();
        app.Use((context, next) =>
        {
            context.RequestServices.GetRequiredService<CurrentUser>().Set(context.User, context.RequestAborted);
            return next(context);
        });
        app.UseAuthorization();
    }
}
