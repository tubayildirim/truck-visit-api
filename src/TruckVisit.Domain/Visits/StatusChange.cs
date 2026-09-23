using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TruckVisit.Domain.Common;

namespace TruckVisit.Domain.Visits;

/// <summary>
/// One immutable entry in a visit's status audit trail.
/// </summary>
/// <remarks>
/// No public setter, no mutating member, internal constructor only reachable from
/// <see cref="Visit"/>. Immutability is structural, not conventional (ADR-003).
/// <para>
/// <see cref="Sequence"/> gives the trail a total order that cannot tie: two changes can share
/// a millisecond, and clocks can be stepped back.
/// </para>
/// <para>
/// <b>Hash chaining (ADR-013).</b> Each entry stores a SHA-256 digest of its own fields
/// combined with the previous entry's hash. A DB trigger and the application guard both
/// <em>prevent</em> tampering; neither can <em>detect</em> it if prevention is circumvented
/// (a superuser can disable a trigger). The chain closes that gap: altering any entry
/// invalidates every subsequent hash, detectable without an untouched reference copy.
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

    /// <summary>Builds the canonical byte representation of an entry and hashes it.</summary>
    /// <remarks>
    /// Fields are joined with U+001F (unit separator), which cannot appear in any input value,
    /// so no two distinct field sets can produce the same canonical string. Timestamps use
    /// round-trip "O" format under the invariant culture so the digest is locale-independent.
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
