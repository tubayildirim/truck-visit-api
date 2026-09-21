using TruckVisit.Application.Visits;
using TruckVisit.Domain.Visits;

namespace TruckVisit.Application.Abstractions;

/// <summary>
/// Already validated and normalised search filters. Terminal scoping has been resolved before this
/// record is built, so the repository never has to reason about authorization.
/// </summary>
/// <param name="TerminalIds">
/// Terminals the result must be restricted to, or <c>null</c> when the caller has global access.
/// An empty collection means "no terminals" and must yield an empty page — never all rows.
/// </param>
public sealed record VisitSearchCriteria(
    IReadOnlyCollection<string>? TerminalIds,
    VisitStatus? CurrentStatus,
    string? MovementFrom,
    string? MovementTo,
    DateTimeOffset? MovementCompletedFrom,
    DateTimeOffset? MovementCompletedTo,
    DateTimeOffset? CreatedTimeFrom,
    DateTimeOffset? CreatedTimeTo,
    string? CreatedBy,
    bool? HasOutstandingMovements,
    int Page,
    int PageSize);

/// <summary>
/// Persistence port for the visit aggregate.
/// </summary>
/// <remarks>
/// Writes go through the aggregate; reads for the search endpoint do not (ADR-006). Search returns
/// projections instead of <see cref="Visit"/> instances because materialising a full aggregate —
/// movements, history and all — for every row of a 25-item page would read an order of magnitude
/// more data than the list view displays. That is a deliberate CQRS-lite split, kept behind this
/// one interface rather than spread across two stacks.
/// </remarks>
public interface IVisitRepository
{
    /// <summary>Loads a visit with its movements and audit trail, or <c>null</c> if absent.</summary>
    Task<Visit?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Stages a newly registered visit for insertion.</summary>
    Task AddAsync(Visit visit, CancellationToken cancellationToken);

    /// <summary>Runs the filtered, paged projection query.</summary>
    Task<PagedResult<VisitSummaryView>> SearchAsync(
        VisitSearchCriteria criteria,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits the unit of work.
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">
    /// Another request changed the same visit first.
    /// </exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Remembers which visit an <c>Idempotency-Key</c> already produced (ADR-011).
/// </summary>
/// <remarks>
/// Gate hardware and mobile clients retry aggressively over flaky links. Without this, one lorry
/// arriving once can end up as three visit records, and the audit trail is then wrong in a way no
/// later correction can fully undo.
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>Returns the visit a previous request with this key created, if any.</summary>
    Task<Guid?> FindVisitIdAsync(string key, string userId, CancellationToken cancellationToken);

    /// <summary>Records the key so a retry returns the same visit instead of creating a new one.</summary>
    Task RememberAsync(string key, string userId, Guid visitId, CancellationToken cancellationToken);
}
