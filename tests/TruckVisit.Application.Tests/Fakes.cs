using TruckVisit.Application.Abstractions;
using TruckVisit.Application.Visits;
using TruckVisit.Domain.Visits;

namespace TruckVisit.Application.Tests;

/// <summary>
/// Hand-written test doubles.
/// </summary>
/// <remarks>
/// No mocking library. There are three ports and they are small, so a fake is shorter than the
/// setup a mock would need and reads as plain code in a failure. It also keeps the promise the
/// production projects make — see ADR-002 and ADR-009 — rather than quietly breaking it in tests.
/// </remarks>
internal sealed class FakeVisitRepository : IVisitRepository
{
    private readonly Dictionary<Guid, Visit> _visits = [];

    /// <summary>The criteria the last search was called with. The point of most search tests.</summary>
    public VisitSearchCriteria? LastSearchCriteria { get; private set; }

    public int SaveCount { get; private set; }

    public IReadOnlyCollection<Visit> Stored => _visits.Values;

    public void Seed(Visit visit) => _visits[visit.Id] = visit;

    public Task<Visit?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_visits.GetValueOrDefault(id));

    public Task AddAsync(Visit visit, CancellationToken cancellationToken)
    {
        _visits[visit.Id] = visit;
        return Task.CompletedTask;
    }

    public Task<PagedResult<VisitSummaryView>> SearchAsync(
        VisitSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        LastSearchCriteria = criteria;
        return Task.FromResult(new PagedResult<VisitSummaryView>([], criteria.Page, criteria.PageSize, 0));
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeIdempotencyStore : IIdempotencyStore
{
    private readonly Dictionary<(string Key, string UserId), Guid> _entries = [];

    public Task<Guid?> FindVisitIdAsync(string key, string userId, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.TryGetValue((key, userId), out var id) ? id : (Guid?)null);

    public Task RememberAsync(string key, string userId, Guid visitId, CancellationToken cancellationToken)
    {
        _entries[(key, userId)] = visitId;
        return Task.CompletedTask;
    }
}

internal sealed class FakeCurrentUser(string userId, params string[] terminals) : ICurrentUser
{
    public string UserId { get; } = userId;

    public IReadOnlySet<string> TerminalIds { get; } =
        new HashSet<string>(terminals, StringComparer.Ordinal);

    public bool HasGlobalTerminalAccess { get; init; }

    public bool CanAccessTerminal(string terminalId) =>
        HasGlobalTerminalAccess || TerminalIds.Contains(terminalId);
}

/// <summary>A clock that does not move, so assertions about time are exact.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class Given
{
    public static readonly DateTimeOffset Now = new(2026, 9, 21, 7, 30, 0, TimeSpan.Zero);

    public static RegisterVisitCommand Command(string terminalId = "DOVER") => new(
        terminalId,
        new TruckInput("mscu 123 4567", "34 abc 123"),
        new DriverInput("Ada Lovelace", "A1234567", null),
        [new MovementInput(MovementType.Delivery, "MSCU1234567", "YARD1", "BERTH3")]);

    public static Visit Visit(string terminalId = "DOVER", string createdBy = "operator-1") =>
        Domain.Visits.Visit.Register(
            terminalId,
            Truck.Create("MSCU1234567", "34ABC123"),
            Driver.Create("Ada Lovelace", "A1234567", null),
            [Movement.Create(MovementType.Delivery, "MSCU1234567", "YARD1", "BERTH3")],
            createdBy,
            Now);
}
