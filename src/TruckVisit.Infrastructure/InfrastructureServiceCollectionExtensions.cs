using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TruckVisit.Application.Abstractions;
using TruckVisit.Infrastructure.Persistence;

namespace TruckVisit.Infrastructure;

/// <summary>Wires the persistence adapters into the container.</summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddTruckVisitInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<TruckVisitDbContext>((serviceProvider, options) =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                // RDS fails over, and a multi-AZ failover is a few seconds of refused connections.
                // Retrying transient faults in the driver is what turns that into a latency blip
                // instead of a burst of 500s against the 99.95% target.
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);

                npgsql.CommandTimeout((int)TimeSpan.FromSeconds(30).TotalSeconds);
            });

            // Resolved from the request's own scope, not a singleton: the interceptor has to see
            // *this* request's caller, and ICurrentUser is itself scoped to the HTTP context.
            options.AddInterceptors(
                new TenantScopeConnectionInterceptor(serviceProvider.GetRequiredService<ICurrentUser>()));
        });

        services.AddScoped<IVisitRepository, VisitRepository>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();

        return services;
    }
}
