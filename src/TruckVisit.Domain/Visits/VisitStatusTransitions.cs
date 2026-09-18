using System.Collections.Frozen;

namespace TruckVisit.Domain.Visits;

/// <summary>
/// The visit lifecycle state machine, expressed in one place.
/// </summary>
/// <remarks>
/// <para>
/// The case does not state which transitions are legal, only that the four statuses exist and
/// that every change must be retained. We model the lifecycle as strictly forward-only with no
/// skipping: a truck cannot be on site before it has been processed at the gate, and
/// <see cref="VisitStatus.Completed"/> is terminal. This is documented as an assumption.
/// </para>
/// <para>
/// Keeping the rules in a lookup rather than a chain of <c>if</c> statements means adding a
/// status (say, <c>Rejected</c>) is a one-line data change, not a hunt through call sites.
/// </para>
/// </remarks>
public static class VisitStatusTransitions
{
    // Frozen rather than a plain dictionary: the table is built once at start-up and then read on
    // every status change, which is exactly the trade FrozenDictionary is designed for.
    private static readonly FrozenDictionary<VisitStatus, IReadOnlyList<VisitStatus>> AllowedTransitions =
        new Dictionary<VisitStatus, IReadOnlyList<VisitStatus>>
        {
            [VisitStatus.PreRegistered] = [VisitStatus.AtGate],
            [VisitStatus.AtGate] = [VisitStatus.OnSite],
            [VisitStatus.OnSite] = [VisitStatus.Completed],
            [VisitStatus.Completed] = [],
        }.ToFrozenDictionary();

    /// <summary>The status every newly registered visit starts in.</summary>
    public static VisitStatus Initial => VisitStatus.PreRegistered;

    /// <summary>Statuses reachable in one step from <paramref name="current"/>.</summary>
    public static IReadOnlyList<VisitStatus> NextFrom(VisitStatus current) =>
        AllowedTransitions.TryGetValue(current, out var next) ? next : [];

    /// <summary>True when a visit may move directly from one status to another.</summary>
    public static bool IsAllowed(VisitStatus from, VisitStatus to) =>
        NextFrom(from).Contains(to);

    /// <summary>True when no further transition is possible.</summary>
    public static bool IsTerminal(VisitStatus status) =>
        NextFrom(status).Count == 0;
}
