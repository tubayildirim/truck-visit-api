using TruckVisit.Application.Abstractions;
using TruckVisit.Application.Visits;
using TruckVisit.Domain.Common;
using TruckVisit.Domain.Visits;
using Xunit;

namespace TruckVisit.Application.Tests;

/// <summary>
/// Covers reading and updating a single visit, and in particular how the API answers when the
/// caller is not entitled to see it.
/// </summary>
public sealed class VisitAccessTests
{
    private readonly FakeVisitRepository _repository = new();

    [Fact]
    public async Task A_visit_at_a_held_terminal_is_returned_with_its_trail()
    {
        var visit = Given.Visit("DOVER");
        _repository.Seed(visit);

        var view = await GetHandler(new FakeCurrentUser("operator-1", "DOVER"))
            .HandleAsync(visit.Id, TestContext.Current.CancellationToken);

        Assert.Equal(visit.Id, view.Id);
        Assert.Single(view.StatusHistory);
    }

    [Fact]
    public async Task A_visit_at_another_terminal_is_reported_as_missing_not_as_forbidden()
    {
        var visit = Given.Visit("HARWICH");
        _repository.Seed(visit);

        // 404, not 403, and deliberately so: distinguishing them would turn the endpoint into an
        // oracle. Anyone with a valid token could enumerate identifiers and learn which visits
        // exist at terminals they have no right to know anything about.
        await Assert.ThrowsAsync<VisitNotFoundException>(
            () => GetHandler(new FakeCurrentUser("operator-1", "DOVER"))
                .HandleAsync(visit.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unknown_identifier_produces_the_same_answer_as_a_hidden_one()
    {
        var hidden = Given.Visit("HARWICH");
        _repository.Seed(hidden);

        var user = new FakeCurrentUser("operator-1", "DOVER");

        var forHidden = await Assert.ThrowsAsync<VisitNotFoundException>(
            () => GetHandler(user).HandleAsync(hidden.Id, TestContext.Current.CancellationToken));

        var unknownId = Guid.CreateVersion7();
        var forUnknown = await Assert.ThrowsAsync<VisitNotFoundException>(
            () => GetHandler(user).HandleAsync(unknownId, TestContext.Current.CancellationToken));

        // Indistinguishable apart from the identifier echoed back — the property that makes the
        // 404 choice above actually work.
        Assert.Equal(hidden.Id, forHidden.VisitId);
        Assert.Equal(unknownId, forUnknown.VisitId);
    }

    [Fact]
    public async Task A_caller_with_global_access_can_read_any_terminal()
    {
        var visit = Given.Visit("HARWICH");
        _repository.Seed(visit);

        var user = new FakeCurrentUser("auditor-1") { HasGlobalTerminalAccess = true };

        var view = await GetHandler(user).HandleAsync(visit.Id, TestContext.Current.CancellationToken);

        Assert.Equal("HARWICH", view.TerminalId);
    }

    [Fact]
    public async Task Advancing_a_status_records_the_caller_and_the_server_time()
    {
        var visit = Given.Visit("DOVER");
        _repository.Seed(visit);

        var view = await ChangeHandler(new FakeCurrentUser("gate-operator-7", "DOVER"))
            .HandleAsync(
                new ChangeVisitStatusCommand(visit.Id, VisitStatus.AtGate, "Arrived early."),
                TestContext.Current.CancellationToken);

        Assert.Equal(nameof(VisitStatus.AtGate), view.CurrentStatus);
        Assert.Equal(2, view.StatusHistory.Count);

        var latest = view.StatusHistory[^1];
        Assert.Equal("gate-operator-7", latest.ChangedBy);
        Assert.Equal("Arrived early.", latest.Reason);
        Assert.Equal(1, _repository.SaveCount);
    }

    [Fact]
    public async Task Advancing_a_visit_at_another_terminal_is_refused_and_writes_nothing()
    {
        var visit = Given.Visit("HARWICH");
        _repository.Seed(visit);

        await Assert.ThrowsAsync<VisitNotFoundException>(
            () => ChangeHandler(new FakeCurrentUser("operator-1", "DOVER")).HandleAsync(
                new ChangeVisitStatusCommand(visit.Id, VisitStatus.AtGate, null),
                TestContext.Current.CancellationToken));

        Assert.Equal(VisitStatus.PreRegistered, visit.CurrentStatus);
        Assert.Equal(0, _repository.SaveCount);
    }

    [Fact]
    public async Task An_illegal_transition_is_refused_before_anything_is_saved()
    {
        var visit = Given.Visit("DOVER");
        _repository.Seed(visit);

        await Assert.ThrowsAsync<InvalidStatusTransitionException>(
            () => ChangeHandler(new FakeCurrentUser("operator-1", "DOVER")).HandleAsync(
                new ChangeVisitStatusCommand(visit.Id, VisitStatus.Completed, null),
                TestContext.Current.CancellationToken));

        // A refused transition leaves no trace: not in the aggregate, not in the unit of work.
        Assert.Single(visit.StatusHistory);
        Assert.Equal(0, _repository.SaveCount);
    }

    private GetVisitByIdHandler GetHandler(ICurrentUser user) => new(_repository, user);

    private ChangeVisitStatusHandler ChangeHandler(ICurrentUser user) =>
        new(_repository, user, new FixedTimeProvider(Given.Now.AddHours(1)));
}
