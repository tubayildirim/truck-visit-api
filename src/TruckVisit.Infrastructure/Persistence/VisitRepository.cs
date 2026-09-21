using Microsoft.EntityFrameworkCore;
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

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // Two gate terminals advanced the same visit at once. Surfaced as a domain-meaningful
            // failure so the API can answer 409 rather than 500 — this is an expected outcome of a
            // busy gate, not a defect.
            var conflictingId = exception.Entries
                .Select(entry => entry.Entity)
                .OfType<Visit>()
                .Select(visit => visit.Id)
                .FirstOrDefault();

            throw new ConcurrencyConflictException(conflictingId);
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
                MovementCount = visit.Movements.Count,
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
            row.MovementCount,
            row.CreatedTime,
            row.CreatedBy,
            row.LastStatusChangedAt));

        return new PagedResult<VisitSummaryView>(items, criteria.Page, criteria.PageSize, totalCount);
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

        return query;
    }
}
