using TruckVisit.Application.Abstractions;
using TruckVisit.Domain.Visits;

namespace TruckVisit.Application.Visits;

/// <summary>
/// Use case: search visits (GET /api/visits).
/// </summary>
/// <remarks>
/// This is the hot path — the operational requirements put the peak at 300 requests per second, and
/// practically all of it lands here. Everything this handler does is therefore either a bound
/// (paging limits) or a narrowing (terminal scope) applied *before* the query reaches the database.
/// </remarks>
public sealed class SearchVisitsHandler(IVisitRepository repository, ICurrentUser currentUser)
{
    public Task<PagedResult<VisitSummaryView>> HandleAsync(
        SearchVisitsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = ResolvePage(query.Page);
        var pageSize = ResolvePageSize(query.PageSize);

        if (query.CreatedTimeFrom is { } from && query.CreatedTimeTo is { } to && from > to)
        {
            throw new RequestValidationException(
                "createdTimeFrom",
                "createdTimeFrom must not be later than createdTimeTo.");
        }

        if (query.CurrentStatus is { } status && !Enum.IsDefined(status))
        {
            throw new RequestValidationException(
                "currentStatus",
                $"currentStatus must be one of: {string.Join(", ", Enum.GetNames<VisitStatus>())}.");
        }

        var criteria = new VisitSearchCriteria(
            TerminalIds: ResolveTerminalScope(query.TerminalId),
            CurrentStatus: query.CurrentStatus,
            // Location filters are normalised with the same rule used when the values were stored,
            // so "yard 1" finds the rows saved as "YARD1".
            MovementFrom: NormalizeLocation(query.MovementFrom, "movementFrom"),
            MovementTo: NormalizeLocation(query.MovementTo, "movementTo"),
            CreatedTimeFrom: query.CreatedTimeFrom,
            CreatedTimeTo: query.CreatedTimeTo,
            CreatedBy: DomainTextTrim(query.CreatedBy),
            Page: page,
            PageSize: pageSize);

        return repository.SearchAsync(criteria, cancellationToken);
    }

    /// <summary>
    /// Turns "what the caller asked for" into "what the caller is allowed to see".
    /// </summary>
    private IReadOnlyCollection<string>? ResolveTerminalScope(string? requestedTerminalId)
    {
        if (!string.IsNullOrWhiteSpace(requestedTerminalId))
        {
            var terminal = TerminalCode.Create(requestedTerminalId);

            if (!currentUser.CanAccessTerminal(terminal.Value))
            {
                // An explicit request for a terminal the caller does not hold is refused outright.
                // Silently returning an empty page would look like "no visits today" to an operator
                // whose token was misconfigured — a far more dangerous answer than an error.
                throw new TerminalAccessDeniedException(terminal.Value);
            }

            return [terminal.Value];
        }

        // No terminal asked for: fall back to everything the caller holds. null means unrestricted
        // and is reserved for principals with global access.
        return currentUser.HasGlobalTerminalAccess ? null : [.. currentUser.TerminalIds];
    }

    private static int ResolvePage(int? requested)
    {
        if (requested is null)
        {
            return PagingDefaults.FirstPage;
        }

        return requested < PagingDefaults.FirstPage
            ? throw new RequestValidationException(
                "page",
                $"page must be {PagingDefaults.FirstPage} or greater.")
            : requested.Value;
    }

    private static int ResolvePageSize(int? requested)
    {
        if (requested is null)
        {
            return PagingDefaults.DefaultPageSize;
        }

        if (requested < 1)
        {
            throw new RequestValidationException("pageSize", "pageSize must be 1 or greater.");
        }

        // Clamping silently would hand the caller a page that does not match what they asked for
        // and make their own paging arithmetic wrong. Better to say no.
        return requested > PagingDefaults.MaxPageSize
            ? throw new RequestValidationException(
                "pageSize",
                $"pageSize cannot exceed {PagingDefaults.MaxPageSize}.")
            : requested.Value;
    }

    private static string? NormalizeLocation(string? raw, string field) =>
        string.IsNullOrWhiteSpace(raw) ? null : LocationCode.Create(raw, field).Value;

    private static string? DomainTextTrim(string? raw)
    {
        var trimmed = raw?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > Visit.MaxActorLength
            ? throw new RequestValidationException(
                "createdBy",
                $"createdBy cannot exceed {Visit.MaxActorLength} characters.")
            : trimmed;
    }
}
