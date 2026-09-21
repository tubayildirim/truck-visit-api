using System.Reflection;
using TruckVisit.Domain.Visits;
using Xunit;

namespace TruckVisit.Domain.Tests;

/// <summary>
/// Covers the detection half of the audit guarantee.
/// </summary>
/// <remarks>
/// The type system and the database trigger both *prevent* an audit entry from being edited.
/// Neither can *detect* an edit once prevention is circumvented — someone with database access can
/// disable a trigger, change a row and re-enable it, leaving nothing behind. These tests use
/// reflection to write directly into the private state, which is precisely the situation the hash
/// chain exists for: they simulate an attacker who got past every guard, and assert that the trail
/// still gives them away.
/// </remarks>
public sealed class AuditChainTests
{
    [Fact]
    public void The_first_entry_anchors_the_chain()
    {
        var visit = TestData.RegisteredVisit();
        var first = visit.StatusHistory[0];

        Assert.Null(first.PreviousHash);
        Assert.Equal(StatusChange.HashLength, first.EntryHash.Length);
        Assert.True(first.IsSelfConsistent());
    }

    [Fact]
    public void Each_entry_links_to_the_one_before_it()
    {
        var visit = TestData.VisitAt(VisitStatus.Completed);

        for (var index = 1; index < visit.StatusHistory.Count; index++)
        {
            Assert.Equal(
                visit.StatusHistory[index - 1].EntryHash,
                visit.StatusHistory[index].PreviousHash);
        }
    }

    [Fact]
    public void Two_entries_with_identical_content_still_hash_differently()
    {
        // Because each digest folds in the previous one, position is part of the identity. Without
        // that, two identical transitions could be swapped or replayed undetected.
        var first = TestData.RegisteredVisit();
        var second = TestData.RegisteredVisit();

        Assert.NotEqual(first.StatusHistory[0].EntryHash, second.StatusHistory[0].EntryHash);
    }

    [Fact]
    public void An_untouched_trail_verifies()
    {
        var visit = TestData.VisitAt(VisitStatus.Completed);

        var verification = visit.VerifyAuditTrail();

        Assert.True(verification.IsIntact);
        Assert.Equal(4, verification.EntriesChecked);
        Assert.Null(verification.BrokenAtSequence);
        Assert.Null(verification.Finding);
    }

    [Fact]
    public void Editing_an_entry_is_detected_at_that_entry()
    {
        var visit = TestData.VisitAt(VisitStatus.Completed);

        // Simulates someone who reached the row directly and rewrote the operator's note — the
        // single most plausible tampering scenario, because a reason is the field that would
        // embarrass someone in an audit.
        Overwrite(visit.StatusHistory[1], nameof(StatusChange.Reason), "Nothing to see here.");

        var verification = visit.VerifyAuditTrail();

        Assert.False(verification.IsIntact);
        Assert.Equal(2, verification.BrokenAtSequence);
        Assert.Contains("hash", verification.Finding!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Editing_who_made_a_change_is_detected()
    {
        var visit = TestData.VisitAt(VisitStatus.Completed);

        // Shifting the blame for a decision onto a different operator.
        Overwrite(visit.StatusHistory[2], nameof(StatusChange.ChangedBy), "someone-else");

        var verification = visit.VerifyAuditTrail();

        Assert.False(verification.IsIntact);
        Assert.Equal(3, verification.BrokenAtSequence);
    }

    [Fact]
    public void Back_dating_a_change_is_detected()
    {
        var visit = TestData.VisitAt(VisitStatus.Completed);

        // Making a late arrival look like it was on time.
        Overwrite(visit.StatusHistory[1], nameof(StatusChange.ChangedAt), TestData.Now.AddYears(-1));

        var verification = visit.VerifyAuditTrail();

        Assert.False(verification.IsIntact);
        Assert.Equal(2, verification.BrokenAtSequence);
    }

    [Fact]
    public void Recomputing_the_edited_entrys_own_hash_does_not_repair_the_chain()
    {
        // A sophisticated attacker would not stop at editing the row — they would recompute its
        // digest so the entry is self-consistent again. That is exactly why each entry folds in
        // its predecessor: the *next* entry's PreviousHash no longer matches, and the break simply
        // moves one step down the chain. Repairing it properly would mean rewriting every
        // subsequent entry, which is the cost the chain is designed to impose.
        var visit = TestData.VisitAt(VisitStatus.Completed);
        var target = visit.StatusHistory[1];

        Overwrite(target, nameof(StatusChange.Reason), "Adjusted.");
        Overwrite(target, nameof(StatusChange.EntryHash), RecomputeHashOf(target));

        Assert.True(target.IsSelfConsistent());

        var verification = visit.VerifyAuditTrail();

        Assert.False(verification.IsIntact);
        Assert.Equal(3, verification.BrokenAtSequence);
        Assert.Contains("predecessor", verification.Finding!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_gap_in_the_sequence_is_detected()
    {
        var visit = TestData.VisitAt(VisitStatus.Completed);

        // Deleting an inconvenient entry leaves a hole the sequence numbers expose immediately,
        // before any hash is even checked.
        Overwrite(visit.StatusHistory[2], nameof(StatusChange.Sequence), 9);

        var verification = visit.VerifyAuditTrail();

        Assert.False(verification.IsIntact);
        Assert.Contains("gap", verification.Finding!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes to a private setter through reflection, simulating a direct database edit.
    /// </summary>
    private static void Overwrite(StatusChange entry, string propertyName, object? value) =>
        typeof(StatusChange)
            .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(entry, value);

    /// <summary>
    /// Reproduces the hash an attacker would compute after editing a row, by round-tripping the
    /// entry through a freshly constructed one with the same (tampered) field values.
    /// </summary>
    private static string RecomputeHashOf(StatusChange entry)
    {
        var rebuilt = (StatusChange)Activator.CreateInstance(
            typeof(StatusChange),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                entry.VisitId,
                entry.Sequence,
                entry.From,
                entry.To,
                entry.ChangedAt,
                entry.ChangedBy,
                entry.Reason,
                entry.PreviousHash,
            ],
            culture: null)!;

        return rebuilt.EntryHash;
    }
}
