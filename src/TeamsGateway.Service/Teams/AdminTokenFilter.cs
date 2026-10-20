using System.Security.Cryptography;
using System.Text;

namespace TeamsGateway.Service.Teams;

/// <summary>擋 <c>/admin</c> 端點：要帶 <c>X-Admin-Token</c> 且等於設定的 <c>Gateway:AdminToken</c>。沒設定就一律拒絕。</summary>
public sealed class AdminTokenFilter(string? adminToken) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (string.IsNullOrEmpty(adminToken))
            return Results.Problem("尚未設定 Gateway:AdminToken，admin 端點已停用", statusCode: StatusCodes.Status503ServiceUnavailable);

        var provided = context.HttpContext.Request.Headers["X-Admin-Token"].ToString();
        if (!FixedTimeEquals(provided, adminToken))
            return Results.Unauthorized();

        return await next(context);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
