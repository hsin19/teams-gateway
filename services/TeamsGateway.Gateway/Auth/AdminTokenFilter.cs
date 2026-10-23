using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace TeamsGateway.Gateway.Auth;

/// <summary>擋 <c>/admin</c> 端點：要帶 <c>X-Admin-Token</c> 且等於 <c>Gateway:AdminToken</c>。沒設定就一律拒絕。</summary>
public sealed class AdminTokenFilter(IOptions<GatewayOptions> options) : IEndpointFilter
{
    private readonly string? _adminToken = options.Value.AdminToken;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (string.IsNullOrEmpty(_adminToken))
            return TypedResults.Problem("尚未設定 Gateway:AdminToken，admin 端點已停用", statusCode: StatusCodes.Status503ServiceUnavailable);

        var provided = context.HttpContext.Request.Headers["X-Admin-Token"].ToString();
        if (!FixedTimeEquals(provided, _adminToken))
            return TypedResults.Unauthorized();

        return await next(context);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
