using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TruckVisit.Infrastructure.Persistence;

namespace TruckVisit.Api.Diagnostics;

/// <summary>
/// Readiness probe: can this instance actually reach its database?
/// </summary>
/// <remarks>
/// Deliberately separate from liveness. A pod that cannot reach the database should be pulled out
/// of the load balancer, not restarted — restarting it will not bring the database back, and a
/// restart loop across every replica during a failover turns a brief outage into a total one.
/// </remarks>
internal sealed class DatabaseHealthCheck(TruckVisitDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var reachable = await dbContext.Database.CanConnectAsync(cancellationToken);

            return reachable
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Database is not reachable.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The message is internal-only: probe results are not served to end users.
            return HealthCheckResult.Unhealthy("Database probe failed.", exception);
        }
    }
}
