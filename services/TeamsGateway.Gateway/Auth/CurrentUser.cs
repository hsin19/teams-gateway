using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace TeamsGateway.Gateway.Auth;

/// <summary>
/// 這個請求的使用者（scoped）：認證完由 <see cref="AuthExtensions.UseGatewayAuth"/> 填進來，授權與端點注入它用（ADR 0011）。
/// 角色需要時才查：scheme 直接給的（API key、admin token）不用查；只有 email 這種「知道是誰、不知道能做什麼」的身分，
/// 端點要求的角色它還沒有時才問 <see cref="IRoleDirectory"/>，同一個請求只問一次。
/// 所以只要 api 的請求不會為了 admin 去查名單（之後名單放 DB 也一樣）。
/// </summary>
public sealed class CurrentUser(IRoleDirectory directory)
{
    private CancellationToken _requestAborted;
    private IReadOnlySet<string>? _directoryRoles;

    public ClaimsPrincipal Principal { get; private set; } = new();

    /// <summary>用 API key 認證時是那把 key 的 id，其他身分是 null。</summary>
    public Guid? ApiKeyId =>
        Guid.TryParse(Principal.FindFirstValue(ApiKeyAuthenticationHandler.KeyIdClaim), out var id) ? id : null;

    public string? Email => Principal.FindFirstValue("email");

    public void Set(ClaimsPrincipal principal, CancellationToken requestAborted)
    {
        Principal = principal;
        _requestAborted = requestAborted;
        _directoryRoles = null;
    }

    public async ValueTask<bool> IsInRoleAsync(string role)
    {
        if (Principal.IsInRole(role))
            return true;
        if (string.IsNullOrEmpty(Email))
            return false;
        _directoryRoles ??= await directory.GetRolesAsync(Email, _requestAborted);
        return _directoryRoles.Contains(role);
    }
}

/// <summary>email → 角色。現在是設定檔（<see cref="ConfiguredRoleDirectory"/>），之後可以換成查 DB。</summary>
public interface IRoleDirectory
{
    ValueTask<IReadOnlySet<string>> GetRolesAsync(string email, CancellationToken ct);
}

/// <summary><c>Gateway:SuperAdmins</c> 裡的 email（不分大小寫）是 admin。</summary>
public sealed class ConfiguredRoleDirectory(IOptions<GatewayOptions> options) : IRoleDirectory
{
    private static readonly IReadOnlySet<string> Admin = new HashSet<string> { GatewayRoles.Admin };
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private readonly HashSet<string> _superAdmins = new(options.Value.SuperAdmins, StringComparer.OrdinalIgnoreCase);

    public ValueTask<IReadOnlySet<string>> GetRolesAsync(string email, CancellationToken ct) =>
        ValueTask.FromResult(_superAdmins.Contains(email) ? Admin : None);
}

/// <summary>端點要求的角色（policy 名稱同 <see cref="GatewayRoles"/>）。</summary>
public sealed record RoleRequirement(string Role) : IAuthorizationRequirement;

public sealed class RoleRequirementHandler(CurrentUser user, ILogger<RoleRequirementHandler> logger) : AuthorizationHandler<RoleRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleRequirement requirement)
    {
        if (!await user.IsInRoleAsync(requirement.Role))
            return;

        context.Succeed(requirement);
        // 從外面登入（有 email）的操作留紀錄：誰拿什麼角色進來
        if (user.Email is { } email)
            logger.LogInformation("{Email} granted {Role}", email, requirement.Role);
    }
}
