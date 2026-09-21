using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;
using TruckVisit.Application.Abstractions;
using TruckVisit.Application.Visits;
using TruckVisit.Domain.Visits;

namespace TruckVisit.Infrastructure.Persistence;

/// <summary>EF Core implementation of the visit persistence port.</summary>
internal sealed class VisitRepository(TruckVisitDbContext context) : IVisitRepository
{
    public async Task<Visit?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        // Movements and history are owned navigations, so EF loads them with the aggregate.
        // Tracking stays on: the caller may be about to change the status.
        await context.Visits.FirstOrDefaultAsync(visit => visit.Id == id, cancellationToken);

    public async Task AddAsync(Visit visit, CancellationToken cancellationToken) =>
        await context.Visits.AddAsync(visit, cancellationToken);

    /// <summary>Unique index that gives each visit a gapless, non-colliding audit sequence.</summary>
    private const string AuditSequenceIndex = "IX_visit_status_history_VisitId_Sequence";

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // The xmin token caught it: the visit row changed under us.
            throw new ConcurrencyConflictException(ConflictingVisitId(exception.Entries));
        }
        catch (DbUpdateException exception) when (IsAuditSequenceCollision(exception))
        {
            // The unique index caught it first.
            //
            // Two gates advancing the same visit both compute the next sequence number from the
            // history they loaded, so both try to insert the same one. EF sends that INSERT before
            // the UPDATE that would have tripped the concurrency token, so without this branch a
            // perfectly ordinary race at a busy gate would surface as a 500.
            //
            // It is the same conflict either way, and the caller gets the same answer: re-read and
            // retry. Found by the integration test that runs the race for real — a unit test with
            // a fake repository could not have produced it.
            throw new ConcurrencyConflictException(ConflictingVisitId(exception.Entries));
        }
    }

    public async Task<PagedResult<VisitSummaryView>> SearchAsync(
        VisitSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        // An empty scope means the caller holds no terminals. Returning everything here would be
        // a complete authorization bypass, so it is handled explicitly rather than by omission.
        if (criteria.TerminalIds is { Count: 0 })
        {
            return new PagedResult<VisitSummaryView>([], criteria.Page, criteria.PageSize, 0);
        }

        var query = ApplyFilters(context.Visits.AsNoTracking(), criteria);

        var totalCount = await query.LongCountAsync(cancellationToken);

        // Offset paging is adequate here: operators page through recent arrivals, not through a
        // seven-year archive. Deep offsets degrade, and the fix (keyset pagination on
        // CreatedTime + Id) is noted as a future improvement rather than built speculatively.
        var rows = await query
            .OrderByDescending(visit => visit.CreatedTime)
            .ThenByDescending(visit => visit.Id)
            .Skip((criteria.Page - 1) * criteria.PageSize)
            .Take(criteria.PageSize)
            .Select(visit => new
            {
                visit.Id,
                visit.TerminalId,
                visit.CurrentStatus,
                TruckUnitNumber = visit.Truck.UnitNumber,
                TruckLicensePlate = visit.Truck.LicensePlate,
                DriverFullName = visit.Driver.FullName,
                DriverCompanyName = visit.Driver.CompanyName,
                MovementCount = visit.Movements.Count,
                OutstandingMovementCount = visit.Movements.Count(movement => movement.CompletedAt == null),
                visit.CreatedTime,
                visit.CreatedBy,
                visit.LastStatusChangedAt,
            })
            .ToListAsync(cancellationToken);

        var items = rows.ConvertAll(row => new VisitSummaryView(
            row.Id,
            row.TerminalId.Value,
            row.CurrentStatus.ToString(),
            row.TruckUnitNumber.Value,
            row.TruckLicensePlate.Value,
            row.DriverFullName,
            row.DriverCompanyName,
            row.MovementCount,
            row.OutstandingMovementCount,
            row.CreatedTime,
            row.CreatedBy,
            row.LastStatusChangedAt));

        return new PagedResult<VisitSummaryView>(items, criteria.Page, criteria.PageSize, totalCount);
    }

    private static bool IsAuditSequenceCollision(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && string.Equals(postgres.ConstraintName, AuditSequenceIndex, StringComparison.Ordinal);

    private static Guid ConflictingVisitId(IReadOnlyList<EntityEntry> entries)
    {
        foreach (var entry in entries)
        {
            // Depending on which constraint fired, the failing entry is either the visit itself or
            // the audit entry that could not be appended to it.
            switch (entry.Entity)
            {
                case Visit visit:
                    return visit.Id;
                case StatusChange change:
                    return change.VisitId;
                default:
                    continue;
            }
        }

        return Guid.Empty;
    }

    private static IQueryable<Visit> ApplyFilters(IQueryable<Visit> query, VisitSearchCriteria criteria)
    {
        if (criteria.TerminalIds is { } terminals)
        {
            var codes = terminals.Select(TerminalCode.Create).ToArray();

            // Single-terminal is by far the common case and produces a plain equality predicate,
            // which the leading column of ix_visits_terminal_status_created can seek on directly.
            query = codes.Length == 1
                ? query.Where(visit => visit.TerminalId == codes[0])
                : query.Where(visit => codes.Contains(visit.TerminalId));
        }

        if (criteria.CurrentStatus is { } status)
        {
            query = query.Where(visit => visit.CurrentStatus == status);
        }

        if (criteria.CreatedTimeFrom is { } createdFrom)
        {
            query = query.Where(visit => visit.CreatedTime >= createdFrom);
        }

        if (criteria.CreatedTimeTo is { } createdTo)
        {
            query = query.Where(visit => visit.CreatedTime <= createdTo);
        }

        if (criteria.CreatedBy is { } createdBy)
        {
            query = query.Where(visit => visit.CreatedBy == createdBy);
        }

        if (criteria.MovementFrom is { } movementFrom)
        {
            var origin = LocationCode.Create(movementFrom, "movementFrom");
            query = query.Where(visit => visit.Movements.Any(movement => movement.From == origin));
        }

        if (criteria.MovementTo is { } movementTo)
        {
            var destination = LocationCode.Create(movementTo, "movementTo");
            query = query.Where(visit => visit.Movements.Any(movement => movement.To == destination));
        }

        // "Which visits had work completed in this window" — the question a shift handover asks.
        if (criteria.MovementCompletedFrom is { } completedFrom)
        {
            query = query.Where(visit => visit.Movements.Any(
                movement => movement.CompletedAt >= completedFrom));
        }

        if (criteria.MovementCompletedTo is { } completedTo)
        {
            query = query.Where(visit => visit.Movements.Any(
                movement => movement.CompletedAt <= completedTo));
        }

        // The gate's live worklist: trucks on site whose cargo has not been dealt with yet.
        if (criteria.HasOutstandingMovements is { } outstanding)
        {
            query = outstanding
                ? query.Where(visit => visit.Movements.Any(movement => movement.CompletedAt == null))
                : query.Where(visit => !visit.Movements.Any(movement => movement.CompletedAt == null));
        }

        return query;
    }
}
