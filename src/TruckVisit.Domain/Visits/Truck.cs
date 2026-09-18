namespace TruckVisit.Domain.Visits;

/// <summary>
/// The vehicle performing the visit.
/// </summary>
/// <remarks>
/// The case requires "at least a unit number and license plate", so both are mandatory and both
/// are normalised codes. Modelled as a value object: a truck has no identity of its own inside
/// this bounded context — two visits describing the same plate are two visits, not a shared entity.
/// A terminal-wide vehicle registry would be a different aggregate in a different service.
/// </remarks>
public sealed record Truck
{
    private Truck(UnitNumber unitNumber, LicensePlate licensePlate)
    {
        UnitNumber = unitNumber;
        LicensePlate = licensePlate;
    }

    public UnitNumber UnitNumber { get; }

    public LicensePlate LicensePlate { get; }

    public static Truck Create(string? unitNumber, string? licensePlate) =>
        new(UnitNumber.Create(unitNumber), LicensePlate.Create(licensePlate));
}
