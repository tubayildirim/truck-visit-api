using TruckVisit.Domain.Visits;

namespace TruckVisit.Application.Visits;

// ---------------------------------------------------------------------------------------------
// Inputs. Every string is nullable on purpose: these records are bound straight from untrusted
// JSON, so "absent" has to be representable. Turning absent into a rejection is the domain's job,
// and it does it in one place instead of once per property here.
// ---------------------------------------------------------------------------------------------

public sealed record TruckInput(string? UnitNumber, string? LicensePlate);

public sealed record DriverInput(string? FullName, string? DocumentId, string? PhoneNumber);

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

/// <summary>
/// The search endpoint's query parameters, exactly as the case lists them.
/// </summary>
public sealed record SearchVisitsQuery(
    string? TerminalId,
    VisitStatus? CurrentStatus,
    string? MovementFrom,
    string? MovementTo,
    DateTimeOffset? CreatedTimeFrom,
    DateTimeOffset? CreatedTimeTo,
    string? CreatedBy,
    int? Page,
    int? PageSize);

// ---------------------------------------------------------------------------------------------
// Outputs. Separate from the domain model so that renaming a field inside the aggregate does not
// silently break every client, and so that the wire format can be reviewed on its own terms.
// Statuses and movement types are emitted as names, never as the underlying numbers.
// ---------------------------------------------------------------------------------------------

public sealed record TruckView(string UnitNumber, string LicensePlate);

public sealed record DriverView(string FullName, string DocumentId, string? PhoneNumber);

public sealed record MovementView(Guid Id, string Type, string UnitNumber, string From, string To);

public sealed record StatusChangeView(
    int Sequence,
    string? From,
    string To,
    DateTimeOffset ChangedAt,
    string ChangedBy,
    string? Reason);

/// <summary>Full representation returned by create and read-by-id.</summary>
public sealed record VisitDetailView(
    Guid Id,
    string TerminalId,
    string CurrentStatus,
    TruckView Truck,
    DriverView Driver,
    IReadOnlyList<MovementView> Movements,
    IReadOnlyList<StatusChangeView> StatusHistory,
    DateTimeOffset CreatedTime,
    string CreatedBy);

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
    int MovementCount,
    DateTimeOffset CreatedTime,
    string CreatedBy,
    DateTimeOffset LastStatusChangedAt);
