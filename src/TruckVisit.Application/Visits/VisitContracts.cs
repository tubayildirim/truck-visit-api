using TruckVisit.Domain.Visits;

namespace TruckVisit.Application.Visits;

// ---------------------------------------------------------------------------------------------
// Inputs. Every string is nullable on purpose: these records are bound straight from untrusted
// JSON, so "absent" has to be representable. Turning absent into a rejection is the domain's job,
// and it does it in one place instead of once per property here.
// ---------------------------------------------------------------------------------------------

public sealed record TruckInput(string? UnitNumber, string? LicensePlate);

public sealed record DriverInput(
    string? FullName,
    string? DocumentId,
    string? CompanyName,
    string? PhoneNumber);

public sealed record MovementInput(MovementType Type, string? UnitNumber, string? From, string? To);

public sealed record RegisterVisitCommand(
    string? TerminalId,
    TruckInput? Truck,
    DriverInput? Driver,
    IReadOnlyList<MovementInput>? Movements);

public sealed record ChangeVisitStatusCommand(
    Guid VisitId,
    VisitStatus TargetStatus,
    string? Reason);

/// <summary>Records that a declared collection or delivery has actually been carried out.</summary>
public sealed record CompleteMovementCommand(Guid VisitId, Guid MovementId);

/// <summary>
/// The search endpoint's query parameters.
/// </summary>
/// <remarks>
/// <c>movementFrom</c> and <c>movementTo</c> are the case's own (undefined) parameters, read here
/// as location codes. <c>movementCompletedFrom</c> / <c>movementCompletedTo</c> are ours: movement
/// timing is a real domain concept, and it gets its own clearly named pair rather than overloading
/// an ambiguous name to mean two different things.
/// </remarks>
public sealed record SearchVisitsQuery(
    string? TerminalId,
    VisitStatus? CurrentStatus,
    string? MovementFrom,
    string? MovementTo,
    DateTimeOffset? MovementCompletedFrom,
    DateTimeOffset? MovementCompletedTo,
    DateTimeOffset? CreatedTimeFrom,
    DateTimeOffset? CreatedTimeTo,
    string? CreatedBy,
    bool? HasOutstandingMovements,
    int? Page,
    int? PageSize);

// ---------------------------------------------------------------------------------------------
// Outputs. Separate from the domain model so that renaming a field inside the aggregate does not
// silently break every client, and so that the wire format can be reviewed on its own terms.
// Statuses and movement types are emitted as names, never as the underlying numbers.
// ---------------------------------------------------------------------------------------------

public sealed record TruckView(string UnitNumber, string LicensePlate);

public sealed record DriverView(
    string FullName,
    string DocumentId,
    string CompanyName,
    string? PhoneNumber);

public sealed record MovementView(
    Guid Id,
    string Type,
    string UnitNumber,
    string From,
    string To,
    DateTimeOffset? CompletedAt,
    string? CompletedBy);

/// <remarks>
/// <see cref="EntryHash"/> and <see cref="PreviousHash"/> are exposed so an auditor can verify the
/// chain independently, with their own tooling, without having to trust this service's own
/// verification endpoint.
/// </remarks>
public sealed record StatusChangeView(
    int Sequence,
    string? From,
    string To,
    DateTimeOffset ChangedAt,
    string ChangedBy,
    string? Reason,
    string EntryHash,
    string? PreviousHash);

/// <summary>Full representation returned by create, read-by-id and the write endpoints.</summary>
public sealed record VisitDetailView(
    Guid Id,
    string TerminalId,
    string CurrentStatus,
    TruckView Truck,
    DriverView Driver,
    IReadOnlyList<MovementView> Movements,
    IReadOnlyList<StatusChangeView> StatusHistory,
    bool HasOutstandingMovements,
    DateTimeOffset CreatedTime,
    string CreatedBy);

/// <summary>Result of checking that a visit's audit trail has not been altered.</summary>
public sealed record AuditVerificationView(
    Guid VisitId,
    bool IsIntact,
    int EntriesChecked,
    int? BrokenAtSequence,
    string? Finding);

/// <summary>
/// Trimmed representation returned by search.
/// </summary>
/// <remarks>
/// Search deliberately does not return the full status history. A gate operator's list view needs
/// the current state, not every transition that led to it, and shipping the history would multiply
/// the rows read per page by the length of each trail — on the busiest endpoint in the system.
/// Callers who need the trail follow the link to the detail endpoint.
/// </remarks>
public sealed record VisitSummaryView(
    Guid Id,
    string TerminalId,
    string CurrentStatus,
    string TruckUnitNumber,
    string TruckLicensePlate,
    string DriverFullName,
    string DriverCompanyName,
    int MovementCount,
    int OutstandingMovementCount,
    DateTimeOffset CreatedTime,
    string CreatedBy,
    DateTimeOffset LastStatusChangedAt);
