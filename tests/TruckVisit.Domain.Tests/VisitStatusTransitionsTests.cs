using TruckVisit.Domain.Visits;
using Xunit;

namespace TruckVisit.Domain.Tests;

/// <summary>Covers the lifecycle state machine in isolation from the aggregate.</summary>
public sealed class VisitStatusTransitionsTests
{
    [Fact]
    public void A_visit_starts_pre_registered() =>
        Assert.Equal(VisitStatus.PreRegistered, VisitStatusTransitions.Initial);

    [Theory]
    [InlineData(VisitStatus.PreRegistered, VisitStatus.AtGate)]
    [InlineData(VisitStatus.AtGate, VisitStatus.OnSite)]
    [InlineData(VisitStatus.OnSite, VisitStatus.Completed)]
    public void The_lifecycle_moves_forward_one_step_at_a_time(VisitStatus from, VisitStatus to) =>
        Assert.True(VisitStatusTransitions.IsAllowed(from, to));

    [Theory]
    // Skipping ahead: a truck cannot be on site before it has been processed at the gate.
    [InlineData(VisitStatus.PreRegistered, VisitStatus.OnSite)]
    [InlineData(VisitStatus.PreRegistered, VisitStatus.Completed)]
    [InlineData(VisitStatus.AtGate, VisitStatus.Completed)]
    // Going backwards: the audit trail describes what happened, not what someone wishes had.
    [InlineData(VisitStatus.AtGate, VisitStatus.PreRegistered)]
    [InlineData(VisitStatus.OnSite, VisitStatus.AtGate)]
    [InlineData(VisitStatus.Completed, VisitStatus.OnSite)]
    // Staying put is not a transition.
    [InlineData(VisitStatus.AtGate, VisitStatus.AtGate)]
    public void Skipping_and_reversing_are_not_permitted(VisitStatus from, VisitStatus to) =>
        Assert.False(VisitStatusTransitions.IsAllowed(from, to));

    [Fact]
    public void Completed_is_terminal()
    {
        Assert.True(VisitStatusTransitions.IsTerminal(VisitStatus.Completed));
        Assert.Empty(VisitStatusTransitions.NextFrom(VisitStatus.Completed));
    }

    [Theory]
    [InlineData(VisitStatus.PreRegistered)]
    [InlineData(VisitStatus.AtGate)]
    [InlineData(VisitStatus.OnSite)]
    public void Every_non_terminal_status_has_somewhere_to_go(VisitStatus status) =>
        Assert.NotEmpty(VisitStatusTransitions.NextFrom(status));

    [Fact]
    public void Every_declared_status_is_reachable_from_the_initial_one()
    {
        // Guards against adding a status to the enum and forgetting to wire it into the table,
        // which would leave dead states no visit could ever enter.
        var reached = new HashSet<VisitStatus> { VisitStatusTransitions.Initial };
        var frontier = new Queue<VisitStatus>([VisitStatusTransitions.Initial]);

        while (frontier.Count > 0)
        {
            foreach (var next in VisitStatusTransitions.NextFrom(frontier.Dequeue()))
            {
                if (reached.Add(next))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        Assert.Equal(Enum.GetValues<VisitStatus>().Length, reached.Count);
    }
}
