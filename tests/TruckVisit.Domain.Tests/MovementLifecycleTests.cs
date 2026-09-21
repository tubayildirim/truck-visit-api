using TruckVisit.Domain.Common;
using TruckVisit.Domain.Visits;
using Xunit;

namespace TruckVisit.Domain.Tests;

/// <summary>
/// Covers the rule that connects the two halves of a visit: a truck does not leave until the work
/// it came for is done.
/// </summary>
public sealed class MovementLifecycleTests
{
    [Fact]
    public void A_newly_declared_movement_is_outstanding()
    {
        var visit = TestData.RegisteredVisit();

        Assert.True(visit.HasOutstandingMovements);
        Assert.False(visit.Movements[0].IsCompleted);
        Assert.Null(visit.Movements[0].CompletedAt);
    }

    [Fact]
    public void Completing_a_movement_records_when_and_by_whom()
    {
        var visit = TestData.VisitAt(VisitStatus.AtGate);
        visit.ChangeStatus(VisitStatus.OnSite, "gate-7", TestData.Now.AddMinutes(30));

        var at = TestData.Now.AddMinutes(45);
        var movement = visit.CompleteMovement(visit.Movements[0].Id, "yard-crane-2", at);

        Assert.True(movement.IsCompleted);
        Assert.Equal(at, movement.CompletedAt);
        Assert.Equal("yard-crane-2", movement.CompletedBy);
        Assert.False(visit.HasOutstandingMovements);
    }

    [Fact]
    public void A_visit_cannot_complete_while_work_is_outstanding()
    {
        // The rule this whole file exists for. Without it the record could show a completed visit
        // whose cargo was never moved, and nothing in the system would notice.
        var visit = TestData.RegisteredVisit();
        visit.ChangeStatus(VisitStatus.AtGate, "gate-7", TestData.Now.AddMinutes(10));
        visit.ChangeStatus(VisitStatus.OnSite, "gate-7", TestData.Now.AddMinutes(20));

        var exception = Assert.Throws<InvalidStatusTransitionException>(
            () => visit.ChangeStatus(VisitStatus.Completed, "gate-7", TestData.Now.AddMinutes(30)));

        Assert.Contains("outstanding", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VisitStatus.OnSite, visit.CurrentStatus);
    }

    [Fact]
    public void A_visit_completes_once_every_movement_is_done()
    {
        var visit = TestData.RegisteredVisit(movements:
        [
            TestData.ValidMovement(MovementType.Delivery, "MSCU1111111"),
            TestData.ValidMovement(MovementType.Collection, "TGHU2222222"),
        ]);

        visit.ChangeStatus(VisitStatus.AtGate, "gate-7", TestData.Now.AddMinutes(10));
        visit.ChangeStatus(VisitStatus.OnSite, "gate-7", TestData.Now.AddMinutes(20));

        visit.CompleteMovement(visit.Movements[0].Id, "crane-1", TestData.Now.AddMinutes(25));

        // One down, one to go — still refused.
        Assert.Throws<InvalidStatusTransitionException>(
            () => visit.ChangeStatus(VisitStatus.Completed, "gate-7", TestData.Now.AddMinutes(30)));

        visit.CompleteMovement(visit.Movements[1].Id, "crane-1", TestData.Now.AddMinutes(35));

        visit.ChangeStatus(VisitStatus.Completed, "gate-7", TestData.Now.AddMinutes(40));

        Assert.Equal(VisitStatus.Completed, visit.CurrentStatus);
        Assert.False(visit.HasOutstandingMovements);
    }

    [Theory]
    [InlineData(VisitStatus.PreRegistered)]
    [InlineData(VisitStatus.AtGate)]
    public void Cargo_cannot_be_moved_before_the_truck_is_admitted(VisitStatus status)
    {
        var visit = TestData.VisitAt(status);

        var exception = Assert.Throws<InvalidStatusTransitionException>(
            () => visit.CompleteMovement(
                visit.Movements[0].Id, "crane-1", TestData.Now.AddMinutes(60)));

        Assert.Equal(nameof(VisitStatus.OnSite), exception.To);
        Assert.False(visit.Movements[0].IsCompleted);
    }

    [Fact]
    public void Cargo_cannot_be_moved_after_the_truck_has_left()
    {
        var visit = TestData.VisitAt(VisitStatus.Completed);

        Assert.Throws<InvalidStatusTransitionException>(
            () => visit.CompleteMovement(
                visit.Movements[0].Id, "crane-1", TestData.Now.AddHours(2)));
    }

    [Fact]
    public void The_same_movement_cannot_be_completed_twice()
    {
        var visit = TestData.VisitAt(VisitStatus.OnSite);
        var movementId = visit.Movements[0].Id;

        // Already completed by the helper on the way to OnSite; a second attempt is a duplicated
        // signal from a yard device or two operators recording the same work. Both are worth
        // surfacing rather than absorbing — the first timestamp is the one that happened.
        var exception = Assert.Throws<DomainValidationException>(
            () => visit.CompleteMovement(movementId, "crane-2", TestData.Now.AddHours(1)));

        Assert.Equal("movements", exception.Field);
    }

    [Fact]
    public void A_movement_belonging_to_another_visit_is_rejected()
    {
        var visit = TestData.VisitAt(VisitStatus.AtGate);
        visit.ChangeStatus(VisitStatus.OnSite, "gate-7", TestData.Now.AddMinutes(30));

        var stranger = TestData.RegisteredVisit();

        var exception = Assert.Throws<DomainValidationException>(
            () => visit.CompleteMovement(
                stranger.Movements[0].Id, "crane-1", TestData.Now.AddMinutes(40)));

        Assert.Equal("movementId", exception.Field);
    }

    [Fact]
    public void A_movement_cannot_be_completed_before_the_truck_was_admitted()
    {
        var visit = TestData.VisitAt(VisitStatus.AtGate);
        visit.ChangeStatus(VisitStatus.OnSite, "gate-7", TestData.Now.AddMinutes(30));

        var exception = Assert.Throws<DomainValidationException>(
            () => visit.CompleteMovement(
                visit.Movements[0].Id, "crane-1", TestData.Now.AddMinutes(5)));

        Assert.Equal("occurredAt", exception.Field);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Every_completion_must_name_who_recorded_it(string? actor)
    {
        var visit = TestData.VisitAt(VisitStatus.AtGate);
        visit.ChangeStatus(VisitStatus.OnSite, "gate-7", TestData.Now.AddMinutes(30));

        var exception = Assert.Throws<DomainValidationException>(
            () => visit.CompleteMovement(
                visit.Movements[0].Id, actor, TestData.Now.AddMinutes(40)));

        Assert.Equal("completedBy", exception.Field);
    }

    [Fact]
    public void Completing_a_movement_does_not_touch_the_status_history()
    {
        // The status trail is about the visit's status. Mixing a second kind of event into it
        // would make "the fourth entry" mean two different things to an auditor. A movement
        // carries its own completion timestamp and actor, which is its audit record.
        var visit = TestData.VisitAt(VisitStatus.AtGate);
        visit.ChangeStatus(VisitStatus.OnSite, "gate-7", TestData.Now.AddMinutes(30));

        var historyBefore = visit.StatusHistory.Count;

        visit.CompleteMovement(visit.Movements[0].Id, "crane-1", TestData.Now.AddMinutes(40));

        Assert.Equal(historyBefore, visit.StatusHistory.Count);
    }
}
