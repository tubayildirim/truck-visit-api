using TruckVisit.Domain.Common;

namespace TruckVisit.Domain.Visits;

/// <summary>
/// One immutable entry in a visit's audit trail.
/// </summary>
/// <remarks>
/// <para>
/// Every property is publicly read-only and can only be set through the internal constructor,
/// which only <see cref="Visit"/> can reach. There is deliberately no method to edit or delete an
/// entry: "all status transitions must be retained as an immutable audit history" is enforced by
/// the shape of the type, not by a convention someone has to remember (ADR-003).
/// </para>
/// <para>
/// <see cref="Sequence"/> exists because timestamps are not a safe ordering key: two changes can
/// share a millisecond, and clocks move. The sequence is assigned by the aggregate and gives the
/// auditor a total order that cannot tie.
/// </para>
/// </remarks>
public sealed class StatusChange
{
    public const int MaxReasonLength = 500;

    /// <summary>Required by EF Core for materialisation; not usable from application code.</summary>
    private StatusChange()
    {
        ChangedBy = null!;
    }

    internal StatusChange(
        Guid visitId,
        int sequence,
        VisitStatus? from,
        VisitStatus to,
        DateTimeOffset changedAt,
        string changedBy,
        string? reason)
    {
        Id = Guid.CreateVersion7();
        VisitId = visitId;
        Sequence = sequence;
        From = from;
        To = to;
        ChangedAt = changedAt;
        ChangedBy = changedBy;
        Reason = reason;
    }

    public Guid Id { get; private set; }

    public Guid VisitId { get; private set; }

    /// <summary>1-based position in the visit's history. Unique and gapless per visit.</summary>
    public int Sequence { get; private set; }

    /// <summary>The status being left, or <c>null</c> for the entry recording registration.</summary>
    public VisitStatus? From { get; private set; }

    /// <summary>The status being entered.</summary>
    public VisitStatus To { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    /// <summary>Authenticated principal that caused the change. Never taken from the request body.</summary>
    public string ChangedBy { get; private set; }

    /// <summary>Optional operator note, e.g. why a truck was held at the gate.</summary>
    public string? Reason { get; private set; }

    internal static string? NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var trimmed = reason.Trim();

        return trimmed.Length > MaxReasonLength
            ? throw new DomainValidationException(
                "reason",
                $"reason cannot exceed {MaxReasonLength} characters (received {trimmed.Length}).")
            : trimmed;
    }
}
