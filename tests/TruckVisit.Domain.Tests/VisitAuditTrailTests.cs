using TruckVisit.Domain.Common;
using TruckVisit.Domain.Visits;
using Xunit;

namespace TruckVisit.Domain.Tests;

/// <summary>
/// Covers the requirement at the centre of the feature: a visit's current status may be updated,
/// and every transition must be retained as an immutable audit history.
/// </summary>
public sealed class VisitAuditTrailTests
{
    [Fact]
    public void An_accepted_transition_moves_the_visit_and_appends_to_the_trail()
    {
        var visit = TestData.RegisteredVisit();
        var arrival = TestData.Now.AddMinutes(15);

        var entry = visit.ChangeStatus(VisitStatus.AtGate, "gate-operator-7", arrival, "Arrived early.");

        Assert.Equal(VisitStatus.AtGate, visit.CurrentStatus);
        Assert.Equal(arrival, visit.LastStatusChangedAt);

        Assert.Equal(2, visit.StatusHistory.Count);
        Assert.Equal(VisitStatus.PreRegistered, entry.From);
        Assert.Equal(VisitStatus.AtGate, entry.To);
        Assert.Equal(2, entry.Sequence);
        Assert.Equal("gate-operator-7", entry.ChangedBy);
        Assert.Equal("Arrived early.", entry.Reason);
    }

    [Fact]
    public void The_whole_journey_is_retained_in_order_with_no_gaps()
    {
        var visit = TestData.VisitAt(VisitStatus.Completed);

        // Registration plus three transitions.
        Assert.Equal(4, visit.StatusHistory.Count);

        Assert.Equal(
            [1, 2, 3, 4],
            visit.StatusHistory.Select(change => change.Sequence).ToArray());

        Assert.Equal(
            [
                VisitStatus.PreRegistered,
                VisitStatus.AtGate,
                VisitStatus.OnSite,
                VisitStatus.Completed,
            ],
            visit.StatusHistory.Select(change => change.To).ToArray());

        // Each entry's "from" is the previous entry's "to" — the chain has no holes.
        for (var index = 1; index < visit.StatusHistory.Count; index++)
        {
            Assert.Equal(visit.StatusHistory[index - 1].To, visit.StatusHistory[index].From);
        }
    }

    [Fact]
    public void Sequence_numbers_order_the_trail_even_when_timestamps_tie()
    {
        // Two events inside the same millisecond would be unorderable by time alone. An auditor
        // asking "what happened first" must still get a definite answer.
        var visit = TestData.RegisteredVisit(createdTime: TestData.Now);

        visit.ChangeStatus(VisitStatus.AtGate, "op", TestData.Now);
        visit.ChangeStatus(VisitStatus.OnSite, "op", TestData.Now);

        Assert.Equal([1, 2, 3], visit.StatusHistory.Select(change => change.Sequence).ToArray());
        Assert.Equal(3, visit.StatusHistory.Select(change => change.Sequence).Distinct().Count());
    }

    [Fact]
    public void Repeating_the_current_status_is_refused_rather_than_ignored()
    {
        var visit = TestData.VisitAt(VisitStatus.AtGate);

        // A duplicated gate signal must not look like it succeeded, and must not add an event
        // that never happened to a record a regulator will read.
        var exception = Assert.Throws<InvalidStatusTransitionException>(
            () => visit.ChangeStatus(VisitStatus.AtGate, "op", TestData.Now.AddHours(1)));

        Assert.Equal(nameof(VisitStatus.AtGate), exception.From);
        Assert.Equal(2, visit.StatusHistory.Count);
    }

    [Theory]
    [InlineData(VisitStatus.PreRegistered, VisitStatus.OnSite)]
    [InlineData(VisitStatus.PreRegistered, VisitStatus.Completed)]
    [InlineData(VisitStatus.AtGate, VisitStatus.Completed)]
    public void Skipping_a_step_is_refused(VisitStatus start, VisitStatus target)
    {
        var visit = TestData.VisitAt(start);
        var historyBefore = visit.StatusHistory.Count;

        Assert.Throws<InvalidStatusTransitionException>(
            () => visit.ChangeStatus(target, "op", TestData.Now.AddHours(1)));

        // Nothing was written: a refused transition leaves no trace in the trail.
        Assert.Equal(start, visit.CurrentStatus);
        Assert.Equal(historyBefore, visit.StatusHistory.Count);
    }

    [Theory]
    [InlineData(VisitStatus.AtGate, VisitStatus.PreRegistered)]
    [InlineData(VisitStatus.OnSite, VisitStatus.AtGate)]
    public void Reversing_the_lifecycle_is_refused(VisitStatus start, VisitStatus target)
    {
        var visit = TestData.VisitAt(start);

        Assert.Throws<InvalidStatusTransitionException>(
            () => visit.ChangeStatus(target, "op", TestData.Now.AddHours(1)));
    }

    [Fact]
    public void A_completed_visit_can_never_change_again()
    {
        var visit = TestData.VisitAt(VisitStatus.Completed);

        foreach (var status in Enum.GetValues<VisitStatus>())
        {
            Assert.Throws<InvalidStatusTransitionException>(
                () => visit.ChangeStatus(status, "op", TestData.Now.AddDays(1)));
        }

        Assert.Equal(4, visit.StatusHistory.Count);
    }

    [Fact]
    public void An_audit_entry_cannot_pre_date_the_one_before_it()
    {
        // A trail that can move backwards in time is not a trail. This catches a caller-supplied
        // timestamp and a clock that has been stepped backwards.
        var visit = TestData.VisitAt(VisitStatus.AtGate);

        var exception = Assert.Throws<DomainValidationException>(
            () => visit.ChangeStatus(VisitStatus.OnSite, "op", TestData.Now.AddMinutes(-1)));

        Assert.Equal("occurredAt", exception.Field);
        Assert.Equal(VisitStatus.AtGate, visit.CurrentStatus);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Every_change_must_name_who_made_it(string? actor)
    {
        var visit = TestData.RegisteredVisit();

        var exception = Assert.Throws<DomainValidationException>(
            () => visit.ChangeStatus(VisitStatus.AtGate, actor, TestData.Now.AddMinutes(1)));

        Assert.Equal("changedBy", exception.Field);
    }

    [Fact]
    public void An_undeclared_status_is_rejected_before_anything_is_written()
    {
        var visit = TestData.RegisteredVisit();

        var exception = Assert.Throws<DomainValidationException>(
            () => visit.ChangeStatus((VisitStatus)42, "op", TestData.Now.AddMinutes(1)));

        Assert.Equal("status", exception.Field);
        Assert.Single(visit.StatusHistory);
    }

    [Fact]
    public void A_blank_reason_is_stored_as_absent_rather_than_as_empty_text()
    {
        var visit = TestData.RegisteredVisit();

        var entry = visit.ChangeStatus(VisitStatus.AtGate, "op", TestData.Now.AddMinutes(1), "   ");

        Assert.Null(entry.Reason);
    }

    [Fact]
    public void An_oversized_reason_is_rejected() =>
        Assert.Throws<DomainValidationException>(() => TestData.RegisteredVisit().ChangeStatus(
            VisitStatus.AtGate,
            "op",
            TestData.Now.AddMinutes(1),
            new string('x', StatusChange.MaxReasonLength + 1)));

    [Fact]
    public void The_exposed_history_cannot_be_cast_back_to_something_mutable()
    {
        // Returning the private list behind IReadOnlyList would let any caller cast to List<T>
        // and rewrite the audit trail, defeating every rule above.
        var visit = TestData.VisitAt(VisitStatus.AtGate);

        Assert.IsNotType<List<StatusChange>>(visit.StatusHistory);
        Assert.IsNotType<List<Movement>>(visit.Movements);
    }

    [Fact]
    public void The_audit_entry_type_exposes_no_way_to_change_itself()
    {
        // Structural guarantee rather than a convention: if someone adds a public setter to
        // StatusChange, this test fails and the reviewer is told why it was not there.
        var mutableMembers = typeof(StatusChange)
            .GetProperties()
            .Where(property => property.SetMethod?.IsPublic == true)
            .Select(property => property.Name)
            .ToArray();

        Assert.Empty(mutableMembers);
    }
}
