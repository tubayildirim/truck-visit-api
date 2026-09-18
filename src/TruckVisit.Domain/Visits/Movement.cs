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
/// <see cref="From"/> and <see cref="To"/> carry our reading of the ambiguous
/// <c>movementFrom</c> / <c>movementTo</c> search parameters: they are location codes, not a date
/// range. The API already names its date filters <c>createdTimeFrom</c> / <c>createdTimeTo</c>, so
/// the absence of "time" in <c>movementFrom</c> is the distinguishing signal. The alternative
/// reading and why we rejected it are recorded in the assumptions document.
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
}
