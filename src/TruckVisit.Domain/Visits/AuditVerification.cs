namespace TruckVisit.Domain.Visits;

/// <summary>
/// The result of walking a visit's audit trail and checking that the hash chain is unbroken.
/// </summary>
/// <param name="IsIntact">True when every entry hashes to its stored digest and links to the one before it.</param>
/// <param name="EntriesChecked">How many entries were examined.</param>
/// <param name="BrokenAtSequence">The first entry that failed, or <c>null</c> when the chain holds.</param>
/// <param name="Finding">A short description of what failed, or <c>null</c> when the chain holds.</param>
public sealed record AuditVerification(
    bool IsIntact,
    int EntriesChecked,
    int? BrokenAtSequence,
    string? Finding)
{
    internal static AuditVerification Intact(int entriesChecked) =>
        new(IsIntact: true, entriesChecked, BrokenAtSequence: null, Finding: null);

    internal static AuditVerification Broken(int entriesChecked, int sequence, string finding) =>
        new(IsIntact: false, entriesChecked, sequence, finding);
}
