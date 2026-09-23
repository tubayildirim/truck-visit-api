using TruckVisit.Domain.Common;

namespace TruckVisit.Domain.Visits;

/// <summary>
/// Aggregate root for a truck visit. All state changes go through methods on this class —
/// no public setters, no direct list access — so the audit trail has exactly one write path.
/// </summary>
/// <remarks>
/// <see cref="CurrentStatus"/> is intentionally denormalised from the history (ADR-006):
/// deriving it per row at 300 req/s would aggregate the largest table in the system on the
/// hot search path. Both fields are updated together in <see cref="ChangeStatus"/>.
/// </remarks>
public sealed class Visit
{
    /// <summary>Hard cap on movements per visit. Keeps the create payload bounded.</summary>
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
        // v7: time-ordered, so inserts are append-friendly on the clustered index.
        // Opaque to callers — a sequential int would leak volume.
        Id = Guid.CreateVersion7(createdTime);
        TerminalId = terminalId;
        Truck = truck;
        Driver = driver;
        CreatedBy = createdBy;
        CreatedTime = createdTime;
        CurrentStatus = VisitStatusTransitions.Initial;
        LastStatusChangedAt = createdTime;
    }

    public Guid Id { get; private set; }

    /// <summary>Terminal that owns this visit. Also the authorization and partitioning key.</summary>
    public TerminalCode TerminalId { get; private set; }

    public VisitStatus CurrentStatus { get; private set; }

    public Truck Truck { get; private set; }

    public Driver Driver { get; private set; }

    // AsReadOnly, not IReadOnlyList<T>: callers can cast the interface back to List<T> and bypass
    // the aggregate's rules. AsReadOnly() returns a wrapper that refuses the cast.
    public IReadOnlyList<Movement> Movements => _movements.AsReadOnly();

    /// <summary>The complete, append-only audit trail, ordered oldest first.</summary>
    public IReadOnlyList<StatusChange> StatusHistory => _statusHistory.AsReadOnly();

    public DateTimeOffset CreatedTime { get; private set; }

    /// <summary>Authenticated principal that registered the visit.</summary>
    public string CreatedBy { get; private set; }

    /// <summary>
    /// Timestamp of the most recent status change. Stored redundantly (same reason as
    /// <see cref="CurrentStatus"/>) and used by <see cref="ChangeStatus"/> to enforce monotonicity
    /// without requiring the history collection to be loaded.
    /// </summary>
    public DateTimeOffset LastStatusChangedAt { get; private set; }

    /// <summary>True while any declared collection or delivery has not been carried out.</summary>
    public bool HasOutstandingMovements => _movements.Exists(movement => !movement.IsCompleted);

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
        // predecessor status rather than at the first transition. It is also the anchor of the
        // hash chain: the only entry with no previous hash.
        visit._statusHistory.Add(new StatusChange(
            visit.Id,
            sequence: 1,
            from: null,
            to: VisitStatusTransitions.Initial,
            changedAt: createdTime,
            changedBy: visit.CreatedBy,
            reason: "Visit registered.",
            previousHash: null));

        return visit;
    }

    /// <summary>
    /// Moves the visit to <paramref name="target"/> and appends the transition to the audit trail.
    /// </summary>
    /// <returns>The audit entry that was appended.</returns>
    /// <exception cref="DomainValidationException">The actor or reason is not acceptable.</exception>
    /// <exception cref="InvalidStatusTransitionException">
    /// The visit is already in <paramref name="target"/>, the transition is not permitted, or work
    /// is still outstanding.
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
            // Rejected, not silently ignored: a duplicate gate signal should not look like success,
            // and a no-op entry in the audit trail would record something that never happened.
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

        if (target == VisitStatus.Completed && HasOutstandingMovements)
        {
            // Completing a visit with outstanding movements would leave cargo recorded as moved
            // when it wasn't. ADR-014 covers why this is a lifecycle gate rather than a warning.
            var outstanding = _movements.Count(movement => !movement.IsCompleted);

            throw new InvalidStatusTransitionException(
                $"Cannot complete the visit while {outstanding} movement(s) remain outstanding. "
                + "Record each collection and delivery first.",
                CurrentStatus.ToString(),
                target.ToString());
        }

        if (occurredAt < LastStatusChangedAt)
        {
            // Guards against a stepped-back clock or a caller-supplied timestamp.
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
            reason: normalizedReason,
            // Chains this entry to the one before it. See StatusChange for why.
            previousHash: _statusHistory[^1].EntryHash);

        _statusHistory.Add(entry);
        CurrentStatus = target;
        LastStatusChangedAt = occurredAt;

        return entry;
    }

    /// <summary>
    /// Records that a declared movement has been carried out. Only allowed while the visit is
    /// <see cref="VisitStatus.OnSite"/>.
    /// </summary>
    /// <remarks>
    /// Does not write to <see cref="StatusHistory"/> — that trail is for visit-level status changes.
    /// Movements carry their own completion timestamp and actor.
    /// </remarks>
    /// <exception cref="InvalidStatusTransitionException">The truck is not on site.</exception>
    /// <exception cref="DomainValidationException">The movement is unknown or already completed.</exception>
    public Movement CompleteMovement(Guid movementId, string? completedBy, DateTimeOffset occurredAt)
    {
        var actor = DomainText.Required(completedBy, "completedBy", MaxActorLength);

        if (CurrentStatus != VisitStatus.OnSite)
        {
            // Cargo cannot be moved before the truck has been admitted, or after it has left.
            throw new InvalidStatusTransitionException(
                $"Movements can only be completed while the visit is '{VisitStatus.OnSite}'; "
                + $"this visit is '{CurrentStatus}'.",
                CurrentStatus.ToString(),
                VisitStatus.OnSite.ToString());
        }

        var movement = _movements.Find(candidate => candidate.Id == movementId)
            ?? throw new DomainValidationException(
                "movementId",
                $"Movement '{movementId}' does not belong to this visit.");

        if (occurredAt < LastStatusChangedAt)
        {
            throw new DomainValidationException(
                "occurredAt",
                $"A movement cannot be completed before the truck was admitted ({LastStatusChangedAt:O}).");
        }

        movement.MarkCompleted(occurredAt, actor);

        return movement;
    }

    /// <summary>
    /// Verifies the hash chain: every entry rehashes to its stored digest and links correctly to
    /// the previous one. Returns the first break found, or <see cref="AuditVerification.Intact"/>.
    /// </summary>
    /// <remarks>
    /// Prevention (type system + DB trigger) and detection (this method) are separate guarantees.
    /// The chain doesn't need an external reference copy — a break is self-evident.
    /// </remarks>
    public AuditVerification VerifyAuditTrail()
    {
        var ordered = _statusHistory.OrderBy(entry => entry.Sequence).ToArray();

        string? expectedPreviousHash = null;

        for (var index = 0; index < ordered.Length; index++)
        {
            var entry = ordered[index];

            if (entry.Sequence != index + 1)
            {
                // Gap or duplicate — the unique index should have caught this at write time.
                return AuditVerification.Broken(
                    ordered.Length,
                    entry.Sequence,
                    $"Expected sequence {index + 1} but found {entry.Sequence}; the trail has a gap.");
            }

            if (!string.Equals(entry.PreviousHash, expectedPreviousHash, StringComparison.Ordinal))
            {
                return AuditVerification.Broken(
                    ordered.Length,
                    entry.Sequence,
                    "The entry does not link to its predecessor; an earlier entry was altered.");
            }

            if (!entry.IsSelfConsistent())
            {
                return AuditVerification.Broken(
                    ordered.Length,
                    entry.Sequence,
                    "The entry's contents do not match its stored hash; this entry was altered.");
            }

            expectedPreviousHash = entry.EntryHash;
        }

        return AuditVerification.Intact(ordered.Length);
    }

    private void SetMovements(IReadOnlyList<Movement> movements)
    {
        if (movements.Count == 0)
        {
            // A truck must have a declared purpose — an empty list would register the arrival
            // with no record of what was collected or delivered (assumption A5).
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
