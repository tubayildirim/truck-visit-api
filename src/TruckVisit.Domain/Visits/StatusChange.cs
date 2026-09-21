using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// <para>
/// <b>Hash chaining (ADR-013).</b> Each entry stores the hash of its own contents combined with the
/// previous entry's hash, so the trail is a chain rather than a pile of independent rows. The
/// database trigger and the application guard both *prevent* tampering; neither can *detect* it if
/// prevention is circumvented — someone with database access can disable a trigger, edit a row and
/// re-enable it, leaving nothing behind. The chain closes that gap: altering any entry invalidates
/// every hash after it, and the break is detectable without needing an untouched copy to compare
/// against. For a system whose stated purpose is surviving regular regulatory audits, being able to
/// demonstrate the trail is intact is worth more than asserting that it cannot be edited.
/// </para>
/// </remarks>
public sealed class StatusChange
{
    public const int MaxReasonLength = 500;

    /// <summary>Length of a SHA-256 digest rendered as lowercase hexadecimal.</summary>
    public const int HashLength = 64;

    /// <summary>Required by EF Core for materialisation; not usable from application code.</summary>
    private StatusChange()
    {
        ChangedBy = null!;
        EntryHash = null!;
    }

    internal StatusChange(
        Guid visitId,
        int sequence,
        VisitStatus? from,
        VisitStatus to,
        DateTimeOffset changedAt,
        string changedBy,
        string? reason,
        string? previousHash)
    {
        Id = Guid.CreateVersion7();
        VisitId = visitId;
        Sequence = sequence;
        From = from;
        To = to;
        ChangedAt = changedAt;
        ChangedBy = changedBy;
        Reason = reason;
        PreviousHash = previousHash;
        EntryHash = ComputeHash(visitId, sequence, from, to, changedAt, changedBy, reason, previousHash);
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

    /// <summary>Hash of the preceding entry, or <c>null</c> for the first one in the chain.</summary>
    public string? PreviousHash { get; private set; }

    /// <summary>Hash of this entry's contents together with <see cref="PreviousHash"/>.</summary>
    public string EntryHash { get; private set; }

    /// <summary>
    /// Recomputes this entry's hash from its stored fields and compares it with the stored digest.
    /// A mismatch means the row was altered after it was written.
    /// </summary>
    public bool IsSelfConsistent() =>
        string.Equals(
            EntryHash,
            ComputeHash(VisitId, Sequence, From, To, ChangedAt, ChangedBy, Reason, PreviousHash),
            StringComparison.Ordinal);

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

    /// <summary>
    /// Builds the canonical byte representation of an entry and hashes it.
    /// </summary>
    /// <remarks>
    /// The separator is a character that cannot occur in any of the inputs, so two different sets
    /// of fields cannot serialise to the same string — without it, a reason ending in a delimiter
    /// could be made to imitate the next field. Timestamps use round-trip "O" format under the
    /// invariant culture so the digest does not depend on where the server is running, which is the
    /// same hazard the code normalisation guards against elsewhere.
    /// </remarks>
    private static string ComputeHash(
        Guid visitId,
        int sequence,
        VisitStatus? from,
        VisitStatus to,
        DateTimeOffset changedAt,
        string changedBy,
        string? reason,
        string? previousHash)
    {
        var canonical = new StringBuilder()
            .Append(visitId.ToString("D", CultureInfo.InvariantCulture)).Append('\u001F')
            .Append(sequence.ToString(CultureInfo.InvariantCulture)).Append('\u001F')
            .Append(from?.ToString() ?? string.Empty).Append('\u001F')
            .Append(to.ToString()).Append('\u001F')
            .Append(changedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('\u001F')
            .Append(changedBy).Append('\u001F')
            .Append(reason ?? string.Empty).Append('\u001F')
            .Append(previousHash ?? string.Empty)
            .ToString();

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexStringLower(digest);
    }
}
