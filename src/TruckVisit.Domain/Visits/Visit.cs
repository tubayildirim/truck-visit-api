using TruckVisit.Domain.Common;

namespace TruckVisit.Domain.Visits;

/// <summary>
/// A single truck visit to a terminal: the aggregate root and the only entry point for changing
/// anything about a visit.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation goes through a method on this class. Collections are exposed as read-only views
/// over private lists, and there is no public setter anywhere, so no caller — service, controller
/// or mapper — can put a visit into a state the rules forbid. That is what makes the audit
/// guarantee trustworthy: there is exactly one code path that can append to the history, and it
/// cannot run without first validating the transition.
/// </para>
/// <para>
/// <see cref="CurrentStatus"/> duplicates information already present in the history. That
/// denormalisation is deliberate (ADR-006): the search endpoint filters on current status at up to
/// 300 requests per second, and resolving it per row from the history table would turn every query
/// into an aggregation over the largest table in the system. The duplicate is safe because both
/// values are written inside <see cref="ChangeStatus"/>, in one transaction.
/// </para>
/// </remarks>
public sealed class Visit
{
    /// <summary>Upper bound on movements per visit — a truck is physically limited, and an
    /// unbounded list is a denial-of-service vector on the create endpoint.</summary>
    public const int MaxMovements = 50;

    public const int MaxActorLength = 128;

    private readonly List<Movement> _movements = [];
    private readonly List<StatusChange> _statusHistory = [];

    /// <summary>Required by EF Core for materialisation; not usable from application code.</summary>
    private Visit()
    {
        TerminalId = null!;
        Truck = null!;
        Driver = null!;
        CreatedBy = null!;
    }

    private Visit(
        TerminalCode terminalId,
        Truck truck,
        Driver driver,
        string createdBy,
        DateTimeOffset createdTime)
    {
        // A version 7 GUID is time-ordered. Random v4 keys scatter inserts across a 51-million-row
        // clustered index and fragment it; v7 keeps them append-friendly while staying opaque to
        // clients, unlike a sequential integer which would leak volume.
        Id = Guid.CreateVersion7(createdTime);
        TerminalId = terminalId;
        Truck = truck;
        Driver = driver;
        CreatedBy = createdBy;
        CreatedTime = createdTime;
        CurrentStatus = VisitStatusTransitions.Initial;
    }

    public Guid Id { get; private set; }

    /// <summary>Terminal that owns this visit. Also the authorization and partitioning key.</summary>
    public TerminalCode TerminalId { get; private set; }

    public VisitStatus CurrentStatus { get; private set; }

    public Truck Truck { get; private set; }

    public Driver Driver { get; private set; }

    public IReadOnlyList<Movement> Movements => _movements;

    /// <summary>The complete, append-only audit trail, ordered oldest first.</summary>
    public IReadOnlyList<StatusChange> StatusHistory => _statusHistory;

    public DateTimeOffset CreatedTime { get; private set; }

    /// <summary>Authenticated principal that registered the visit.</summary>
    public string CreatedBy { get; private set; }

    /// <summary>Timestamp of the most recent audit entry, used to keep the trail monotonic.</summary>
    public DateTimeOffset LastStatusChangedAt =>
        _statusHistory.Count == 0 ? CreatedTime : _statusHistory[^1].ChangedAt;

    /// <summary>
    /// Registers a new visit in <see cref="VisitStatus.PreRegistered"/> and opens its audit trail.
    /// </summary>
    /// <exception cref="DomainValidationException">Any supplied value breaks a domain rule.</exception>
    public static Visit Register(
        string? terminalId,
        Truck truck,
        Driver driver,
        IReadOnlyList<Movement> movements,
        string? createdBy,
        DateTimeOffset createdTime)
    {
        ArgumentNullException.ThrowIfNull(truck);
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(movements);

        var visit = new Visit(
            TerminalCode.Create(terminalId),
            truck,
            driver,
            DomainText.Required(createdBy, "createdBy", MaxActorLength),
            createdTime);

        visit.SetMovements(movements);

        // The registration itself is an auditable event, so the trail starts here with no
        // predecessor status rather than at the first transition.
        visit._statusHistory.Add(new StatusChange(
            visit.Id,
            sequence: 1,
            from: null,
            to: VisitStatusTransitions.Initial,
            changedAt: createdTime,
            changedBy: visit.CreatedBy,
            reason: "Visit registered."));

        return visit;
    }

    /// <summary>
    /// Moves the visit to <paramref name="target"/> and appends the transition to the audit trail.
    /// </summary>
    /// <returns>The audit entry that was appended.</returns>
    /// <exception cref="DomainValidationException">The actor or reason is not acceptable.</exception>
    /// <exception cref="InvalidStatusTransitionException">
    /// The visit is already in <paramref name="target"/>, or the transition is not permitted.
    /// </exception>
    public StatusChange ChangeStatus(
        VisitStatus target,
        string? changedBy,
        DateTimeOffset occurredAt,
        string? reason = null)
    {
        if (!Enum.IsDefined(target))
        {
            throw new DomainValidationException(
                "status",
                $"status must be one of: {string.Join(", ", Enum.GetNames<VisitStatus>())}.");
        }

        var actor = DomainText.Required(changedBy, "changedBy", MaxActorLength);
        var normalizedReason = StatusChange.NormalizeReason(reason);

        if (target == CurrentStatus)
        {
            // Rejected rather than ignored: a silent no-op would let a duplicated gate signal look
            // like it succeeded, and writing the entry anyway would pad the audit trail with events
            // that never happened.
            throw new InvalidStatusTransitionException(
                $"Visit is already in status '{CurrentStatus}'.",
                CurrentStatus.ToString(),
                target.ToString());
        }

        if (!VisitStatusTransitions.IsAllowed(CurrentStatus, target))
        {
            var allowed = VisitStatusTransitions.NextFrom(CurrentStatus);

            var detail = allowed.Count == 0
                ? $"'{CurrentStatus}' is a terminal status and cannot change."
                : $"Allowed next status from '{CurrentStatus}': {string.Join(", ", allowed)}.";

            throw new InvalidStatusTransitionException(
                $"Cannot move visit from '{CurrentStatus}' to '{target}'. {detail}",
                CurrentStatus.ToString(),
                target.ToString());
        }

        if (occurredAt < LastStatusChangedAt)
        {
            // An audit trail that can go backwards in time is not an audit trail. This guards
            // against a caller-supplied timestamp or a clock that has been stepped back.
            throw new DomainValidationException(
                "occurredAt",
                $"A status change cannot pre-date the previous entry ({LastStatusChangedAt:O}).");
        }

        var entry = new StatusChange(
            Id,
            sequence: _statusHistory.Count + 1,
            from: CurrentStatus,
            to: target,
            changedAt: occurredAt,
            changedBy: actor,
            reason: normalizedReason);

        _statusHistory.Add(entry);
        CurrentStatus = target;

        return entry;
    }

    private void SetMovements(IReadOnlyList<Movement> movements)
    {
        if (movements.Count == 0)
        {
            // A visit exists in order to collect or deliver something. Allowing an empty list would
            // let the gate admit a truck with no recorded purpose — see the assumptions document.
            throw new DomainValidationException(
                "movements",
                "A visit must declare at least one collection or delivery.");
        }

        if (movements.Count > MaxMovements)
        {
            throw new DomainValidationException(
                "movements",
                $"A visit cannot declare more than {MaxMovements} movements (received {movements.Count}).");
        }

        var seen = new HashSet<(MovementType Type, string UnitNumber)>();

        foreach (var movement in movements)
        {
            ArgumentNullException.ThrowIfNull(movement);

            if (!seen.Add((movement.Type, movement.UnitNumber.Value)))
            {
                throw new DomainValidationException(
                    "movements",
                    $"Unit '{movement.UnitNumber}' is listed more than once as a {movement.Type}.");
            }
        }

        _movements.AddRange(movements);
    }
}
