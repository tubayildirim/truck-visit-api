using TruckVisit.Domain.Common;

namespace TruckVisit.Domain.Visits;

/// <summary>What the truck is doing with a cargo unit on this visit.</summary>
public enum MovementType
{
    /// <summary>The truck is picking the unit up and taking it away.</summary>
    Collection = 1,

    /// <summary>The truck is dropping the unit off at the terminal.</summary>
    Delivery = 2,
}

/// <summary>
/// A single collection or delivery carried out during a visit.
/// </summary>
/// <remarks>
/// <para>
/// A movement is declared when the visit is registered and completed while the truck is on site,
/// so it has a lifecycle of its own: <see cref="CompletedAt"/> is null until the unit has actually
/// been moved. That is what makes the outstanding-work rule in <see cref="Visit"/> possible — a
/// truck cannot leave until the work it came for is done.
/// </para>
/// <para>
/// <see cref="From"/> and <see cref="To"/> carry our reading of the ambiguous
/// <c>movementFrom</c> / <c>movementTo</c> search parameters: they are location codes, not a date
/// range. The API already names its date filters <c>createdTimeFrom</c> / <c>createdTimeTo</c>, so
/// the absence of "time" in <c>movementFrom</c> is the distinguishing signal. Movement *timing* is
/// modelled too, because the domain needs it — it is exposed under its own filter pair rather than
/// overloading an ambiguous name to mean two things.
/// </para>
/// </remarks>
public sealed class Movement
{
    /// <summary>Required by EF Core for materialisation; not usable from application code.</summary>
    private Movement()
    {
        UnitNumber = null!;
        From = null!;
        To = null!;
    }

    private Movement(MovementType type, UnitNumber unitNumber, LocationCode from, LocationCode to)
    {
        Id = Guid.CreateVersion7();
        Type = type;
        UnitNumber = unitNumber;
        From = from;
        To = to;
    }

    public Guid Id { get; private set; }

    public MovementType Type { get; private set; }

    /// <summary>The cargo unit being moved — a container, trailer or swap body.</summary>
    public UnitNumber UnitNumber { get; private set; }

    /// <summary>Where the unit comes from.</summary>
    public LocationCode From { get; private set; }

    /// <summary>Where the unit is going.</summary>
    public LocationCode To { get; private set; }

    /// <summary>When the unit was actually moved, or <c>null</c> while the work is outstanding.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Who recorded the completion. Always the authenticated principal.</summary>
    public string? CompletedBy { get; private set; }

    public bool IsCompleted => CompletedAt is not null;

    public static Movement Create(MovementType type, string? unitNumber, string? from, string? to)
    {
        if (!Enum.IsDefined(type))
        {
            throw new DomainValidationException(
                "movements.type",
                $"movement type must be one of: {string.Join(", ", Enum.GetNames<MovementType>())}.");
        }

        return new Movement(
            type,
            UnitNumber.Create(unitNumber),
            LocationCode.Create(from, "movements.from"),
            LocationCode.Create(to, "movements.to"));
    }

    /// <summary>
    /// Records that the unit has been moved. Internal because only <see cref="Visit"/> may call it —
    /// completing a movement has to be checked against the visit's own state first.
    /// </summary>
    internal void MarkCompleted(DateTimeOffset at, string by)
    {
        if (IsCompleted)
        {
            // Not idempotent on purpose. A second completion means either a duplicated signal from
            // a yard device or two operators recording the same work, and both are worth surfacing
            // rather than absorbing — the first timestamp is the one that happened.
            throw new DomainValidationException(
                "movements",
                $"Movement '{Id}' was already completed at {CompletedAt:O}.");
        }

        CompletedAt = at;
        CompletedBy = by;
    }
}
