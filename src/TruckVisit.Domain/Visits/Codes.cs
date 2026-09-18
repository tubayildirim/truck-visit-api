using TruckVisit.Domain.Common;

namespace TruckVisit.Domain.Visits;

/// <summary>
/// Identifier of the cargo unit a truck carries (container, trailer, swap body).
/// Acceptance criteria: stored capitalised and free of whitespace.
/// </summary>
public sealed record UnitNumber : NormalizedCode
{
    public const int MaxLength = 20;

    private UnitNumber(string value)
        : base(value)
    {
    }

    public static UnitNumber Create(string? raw) =>
        new(NormalizeAndValidate(raw, "unitNumber", MaxLength));
}

/// <summary>
/// Registration plate of the truck.
/// Acceptance criteria: stored capitalised and free of whitespace.
/// </summary>
public sealed record LicensePlate : NormalizedCode
{
    public const int MaxLength = 15;

    private LicensePlate(string value)
        : base(value)
    {
    }

    public static LicensePlate Create(string? raw) =>
        new(NormalizeAndValidate(raw, "licensePlate", MaxLength));
}

/// <summary>
/// Identifier of the terminal a visit belongs to.
/// This is the tenant boundary (ADR-007): it partitions the data and scopes every
/// authorization decision, so it is normalised for the same reason the codes above are —
/// "DOVER" and " dover " must never resolve to two different terminals.
/// </summary>
public sealed record TerminalCode : NormalizedCode
{
    public const int MaxLength = 32;

    private TerminalCode(string value)
        : base(value)
    {
    }

    public static TerminalCode Create(string? raw) =>
        new(NormalizeAndValidate(raw, "terminalId", MaxLength));
}

/// <summary>
/// Origin or destination of a movement — a yard block, berth, depot or external site code.
/// </summary>
public sealed record LocationCode : NormalizedCode
{
    public const int MaxLength = 32;

    private LocationCode(string value)
        : base(value)
    {
    }

    public static LocationCode Create(string? raw, string field) =>
        new(NormalizeAndValidate(raw, field, MaxLength));
}
