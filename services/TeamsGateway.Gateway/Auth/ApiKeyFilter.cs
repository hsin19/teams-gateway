using TeamsGateway.Gateway.Data;

namespace TeamsGateway.Gateway.Auth;

/// <summary>擋 <c>/v1</c> 端點：要帶 <c>Authorization: Bearer tgw_...</c> 或 <c>X-Api-Key</c>。驗過的 key 放進 <c>HttpContext.Items["ApiKey"]</c>。</summary>
public sealed class ApiKeyFilter(ApiKeyService keys) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var raw = ExtractKey(http.Request);
        if (raw is null)
            return Results.Unauthorized();

        var key = await keys.ValidateAsync(raw, http.RequestAborted);
        if (key is null)
            return Results.Unauthorized();

        http.Items[nameof(ApiKey)] = key;
        return await next(context);
    }

    private static string? ExtractKey(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        var header = request.Headers["X-Api-Key"].ToString();
        return string.IsNullOrEmpty(header) ? null : header;
    }
}
