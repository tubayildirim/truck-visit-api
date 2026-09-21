using System.Diagnostics.Metrics;

namespace TruckVisit.Api.Diagnostics;

/// <summary>
/// Domain counters for the visit module.
/// </summary>
/// <remarks>
/// <para>
/// Throughput and latency are already emitted by ASP.NET Core's built-in
/// <c>http.server.request.duration</c> histogram, so they are not re-implemented here. What the
/// platform cannot know is the business shape of the traffic, which is what this meter adds:
/// how many visits are registered, how the fleet moves through the lifecycle, and how often a
/// transition is refused.
/// </para>
/// <para>
/// That last counter is the useful one operationally. A sudden rise in rejected transitions
/// usually means a gate device is out of step with the server — a fault no HTTP metric would
/// reveal, because every one of those requests is a perfectly healthy 409.
/// </para>
/// </remarks>
internal sealed class VisitMetrics
{
    public const string MeterName = "TruckVisit.Api";

    private readonly Counter<long> _registered;
    private readonly Counter<long> _statusChanged;
    private readonly Counter<long> _transitionRejected;

    public VisitMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MeterName);

        _registered = meter.CreateCounter<long>(
            "truckvisit.visits.registered",
            unit: "{visit}",
            description: "Truck visits registered.");

        _statusChanged = meter.CreateCounter<long>(
            "truckvisit.visits.status_changed",
            unit: "{transition}",
            description: "Accepted visit status transitions.");

        _transitionRejected = meter.CreateCounter<long>(
            "truckvisit.visits.transition_rejected",
            unit: "{transition}",
            description: "Status transitions refused because the visit was in an incompatible state.");
    }

    public void VisitRegistered(string terminalId) =>
        _registered.Add(1, new KeyValuePair<string, object?>("terminal.id", terminalId));

    public void StatusChanged(string terminalId, string toStatus) =>
        _statusChanged.Add(
            1,
            new KeyValuePair<string, object?>("terminal.id", terminalId),
            new KeyValuePair<string, object?>("visit.status", toStatus));

    public void TransitionRejected(string from, string to) =>
        _transitionRejected.Add(
            1,
            new KeyValuePair<string, object?>("visit.status.from", from),
            new KeyValuePair<string, object?>("visit.status.to", to));
}
