using Microsoft.EntityFrameworkCore;
using TruckVisit.Domain.Visits;

namespace TruckVisit.Infrastructure.Persistence;

/// <summary>
/// The unit of work for the visits module.
/// </summary>
public sealed class TruckVisitDbContext(DbContextOptions<TruckVisitDbContext> options)
    : DbContext(options)
{
    public DbSet<Visit> Visits => Set<Visit>();

    internal DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public override int SaveChanges()
    {
        GuardAuditTrail();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        GuardAuditTrail();
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TruckVisitDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Last line of defence for the immutability requirement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The domain model already makes an in-place edit impossible — <see cref="StatusChange"/> has
    /// no public setters and no mutating methods. This check exists for the ways around that: code
    /// that attaches a modified graph, a future bulk operation, or a developer who adds a setter
    /// without knowing why it was not there.
    /// </para>
    /// <para>
    /// It is not the only guard. The migration also revokes UPDATE and DELETE on the history table
    /// at the database level, so a direct SQL statement is refused too (ADR-003). Defence in depth
    /// matters here because this table is what a regulatory audit actually inspects.
    /// </para>
    /// </remarks>
    private void GuardAuditTrail()
    {
        foreach (var entry in ChangeTracker.Entries<StatusChange>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"The visit status history is append-only; attempted to {entry.State} " +
                    $"an existing audit entry. This is a bug, not a recoverable condition.");
            }
        }
    }
}
