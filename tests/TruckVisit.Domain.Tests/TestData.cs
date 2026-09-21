using TruckVisit.Domain.Visits;

namespace TruckVisit.Domain.Tests;

/// <summary>
/// Builders for valid domain objects, so each test states only the thing it is actually about.
/// </summary>
internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 21, 7, 30, 0, TimeSpan.Zero);

    public static Truck ValidTruck() => Truck.Create("MSCU1234567", "34ABC123");

    public static Driver ValidDriver() =>
        Driver.Create("Ada Lovelace", "A1234567", "Lovelace Haulage Ltd", "+905001234567");

    public static Movement ValidMovement(
        MovementType type = MovementType.Delivery,
        string unitNumber = "MSCU1234567",
        string from = "YARD1",
        string to = "BERTH3") =>
        Movement.Create(type, unitNumber, from, to);

    public static Visit RegisteredVisit(
        string terminalId = "DOVER",
        string createdBy = "operator-1",
        DateTimeOffset? createdTime = null,
        IReadOnlyList<Movement>? movements = null) =>
        Visit.Register(
            terminalId,
            ValidTruck(),
            ValidDriver(),
            movements ?? [ValidMovement()],
            createdBy,
            createdTime ?? Now);

    /// <summary>
    /// Returns a visit already advanced to <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// Completes the declared movements on the way through <see cref="VisitStatus.OnSite"/>,
    /// because a visit cannot reach <see cref="VisitStatus.Completed"/> with work outstanding.
    /// That the helper has to do this is itself the rule working.
    /// </remarks>
    public static Visit VisitAt(VisitStatus target)
    {
        var visit = RegisteredVisit();
        var offset = 1;

        while (visit.CurrentStatus != target)
        {
            var next = VisitStatusTransitions.NextFrom(visit.CurrentStatus);

            if (next.Count == 0)
            {
                throw new InvalidOperationException($"'{target}' is not reachable.");
            }

            visit.ChangeStatus(next[0], "operator-1", Now.AddMinutes(offset++));

            if (visit.CurrentStatus == VisitStatus.OnSite)
            {
                CompleteAllMovements(visit, Now.AddMinutes(offset++));
            }
        }

        return visit;
    }

    /// <summary>Marks every declared movement as carried out. Requires the visit to be on site.</summary>
    public static void CompleteAllMovements(Visit visit, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(visit);

        foreach (var movement in visit.Movements.Where(candidate => !candidate.IsCompleted).ToArray())
        {
            visit.CompleteMovement(movement.Id, "yard-crane-1", at);
        }
    }
}
