namespace TruckVisit.Domain.Visits;

/// <summary>
/// Lifecycle states of a truck visit, in the order a truck passes through them.
/// </summary>
/// <remarks>
/// The numeric values are assigned explicitly and must never be reused or reordered:
/// status history is retained for seven years, so a value written in 2026 has to mean the
/// same thing when an auditor reads it in 2033. Persistence writes the *name*, not the
/// number, for exactly the same reason (ADR-004).
/// </remarks>
public enum VisitStatus
{
    /// <summary>Booked ahead of arrival; the truck is not at the terminal yet.</summary>
    PreRegistered = 1,

    /// <summary>The truck has arrived and is being processed at the gate.</summary>
    AtGate = 2,

    /// <summary>The truck has been admitted and is inside the terminal.</summary>
    OnSite = 3,

    /// <summary>The visit is finished and the truck has left. Terminal state.</summary>
    Completed = 4,
}
