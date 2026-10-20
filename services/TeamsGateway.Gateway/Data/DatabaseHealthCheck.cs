using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TeamsGateway.Gateway.Data;

public sealed class DatabaseHealthCheck(IDbContextFactory<GatewayDbContext> factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("資料庫連不上");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("資料庫檢查失敗", ex);
        }
    }
}
