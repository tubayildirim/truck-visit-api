using Microsoft.EntityFrameworkCore;
using TruckVisit.Application.Abstractions;
using TruckVisit.Domain.Visits;
using TruckVisit.Infrastructure.Persistence;
using Xunit;

namespace TruckVisit.Api.IntegrationTests;

/// <summary>
/// Proves the guarantees that only a real database can demonstrate.
/// </summary>
/// <remarks>
/// The domain unit tests already cover the rules. What they cannot show is whether those rules
/// survive a round trip: whether the value converters preserve normalisation, whether the
/// append-only trigger actually exists in the schema, and whether the concurrency token is wired.
/// Those are the tests here — deliberately few, and each one about something the unit suite
/// cannot reach.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class VisitPersistenceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_visit_round_trips_with_its_movements_and_audit_trail()
    {
        fixture.SkipIfUnavailable();

        var terminal = UniqueTerminal();
        var visit = NewVisit(terminal);
        visit.ChangeStatus(VisitStatus.AtGate, "operator-2", Now.AddMinutes(30), "Arrived early.");

        await using (var write = fixture.CreateContext())
        {
            write.Visits.Add(visit);
            await write.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = fixture.CreateContext();
        var stored = await read.Visits.SingleAsync(
            candidate => candidate.Id == visit.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(terminal, stored.TerminalId.Value);
        Assert.Equal(VisitStatus.AtGate, stored.CurrentStatus);

        // The whole point of normalising in the constructor: what comes back out is canonical,
        // not whatever casing and spacing the caller happened to send.
        Assert.Equal("MSCU1234567", stored.Truck.UnitNumber.Value);
        Assert.Equal("34ABC123", stored.Truck.LicensePlate.Value);

        Assert.Single(stored.Movements);
        Assert.Equal(MovementType.Delivery, stored.Movements[0].Type);
        Assert.Equal("YARD1", stored.Movements[0].From.Value);

        // Owned collections load with the aggregate, so the trail arrives intact and ordered.
        Assert.Equal(2, stored.StatusHistory.Count);
        Assert.Equal([1, 2], stored.StatusHistory.Select(entry => entry.Sequence).Order().ToArray());
        Assert.Equal("Arrived early.", stored.StatusHistory.Single(entry => entry.Sequence == 2).Reason);
    }

    [Fact]
    public async Task The_application_refuses_to_save_a_modified_audit_entry()
    {
        fixture.SkipIfUnavailable();

        var visit = NewVisit(UniqueTerminal());

        await using var context = fixture.CreateContext();
        context.Visits.Add(visit);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Force the state the domain model makes unreachable, to prove the second line of defence
        // catches code that attaches a graph or bypasses the aggregate.
        var entry = context.Entry(visit.StatusHistory[0]);
        entry.State = EntityState.Modified;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains("append-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_database_itself_refuses_to_update_or_delete_an_audit_entry()
    {
        fixture.SkipIfUnavailable();

        var visit = NewVisit(UniqueTerminal());

        await using var context = fixture.CreateContext();
        context.Visits.Add(visit);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Raw SQL, bypassing every application guard. This is the line of defence that matters to
        // an auditor: it holds even for a connection that never goes through this codebase.
        var update = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            """UPDATE visit_status_history SET "Reason" = 'tampered' WHERE "VisitId" = {0}""",
            [visit.Id],
            TestContext.Current.CancellationToken));

        Assert.Contains("append-only", update.Message, StringComparison.OrdinalIgnoreCase);

        var delete = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            """DELETE FROM visit_status_history WHERE "VisitId" = {0}""",
            [visit.Id],
            TestContext.Current.CancellationToken));

        Assert.Contains("append-only", delete.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_gates_advancing_the_same_visit_produce_a_conflict_not_a_lost_update()
    {
        fixture.SkipIfUnavailable();

        var visit = NewVisit(UniqueTerminal());

        await using (var seed = fixture.CreateContext())
        {
            seed.Visits.Add(visit);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();

        var byFirst = await first.Visits.SingleAsync(
            candidate => candidate.Id == visit.Id, TestContext.Current.CancellationToken);
        var bySecond = await second.Visits.SingleAsync(
            candidate => candidate.Id == visit.Id, TestContext.Current.CancellationToken);

        byFirst.ChangeStatus(VisitStatus.AtGate, "gate-1", Now.AddMinutes(10));
        bySecond.ChangeStatus(VisitStatus.AtGate, "gate-2", Now.AddMinutes(11));

        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Without the xmin concurrency token this would succeed and silently overwrite gate-1's
        // audit entry — the failure mode the token exists to prevent.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => second.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Search_returns_only_the_terminals_it_was_scoped_to()
    {
        fixture.SkipIfUnavailable();

        var mine = UniqueTerminal();
        var theirs = UniqueTerminal();

        await using (var seed = fixture.CreateContext())
        {
            seed.Visits.Add(NewVisit(mine));
            seed.Visits.Add(NewVisit(mine));
            seed.Visits.Add(NewVisit(theirs));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var context = fixture.CreateContext();
        var repository = new VisitRepository(context);

        var page = await repository.SearchAsync(
            Criteria(terminals: [mine]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, item => Assert.Equal(mine, item.TerminalId));

        // An empty scope means "this caller holds no terminals". Returning everything here would
        // be a complete authorization bypass, so it is asserted rather than assumed.
        var empty = await repository.SearchAsync(
            Criteria(terminals: []),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, empty.TotalCount);
        Assert.Empty(empty.Items);
    }

    [Fact]
    public async Task Search_matches_movement_locations_after_normalisation()
    {
        fixture.SkipIfUnavailable();

        var terminal = UniqueTerminal();

        await using (var seed = fixture.CreateContext())
        {
            seed.Visits.Add(NewVisit(terminal));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var context = fixture.CreateContext();
        var repository = new VisitRepository(context);

        // Stored as "YARD1"; the caller types it the way a person would.
        var page = await repository.SearchAsync(
            Criteria(terminals: [terminal]) with { MovementFrom = "YARD1" },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, page.TotalCount);

        var miss = await repository.SearchAsync(
            Criteria(terminals: [terminal]) with { MovementFrom = "NOWHERE" },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, miss.TotalCount);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 21, 7, 30, 0, TimeSpan.Zero);

    // Each test gets its own terminal so the shared container needs no clean-up between them,
    // and a failure never cascades into the next test as confusing noise.
    private static string UniqueTerminal() => $"T{Guid.CreateVersion7():N}"[..16].ToUpperInvariant();

    private static Visit NewVisit(string terminalId) => Visit.Register(
        terminalId,
        Truck.Create("mscu 123 4567", "34 abc 123"),
        Driver.Create("Ada Lovelace", "A1234567", null),
        [Movement.Create(MovementType.Delivery, "MSCU1234567", "YARD1", "BERTH3")],
        "operator-1",
        Now);

    private static VisitSearchCriteria Criteria(IReadOnlyCollection<string>? terminals) => new(
        TerminalIds: terminals,
        CurrentStatus: null,
        MovementFrom: null,
        MovementTo: null,
        CreatedTimeFrom: null,
        CreatedTimeTo: null,
        CreatedBy: null,
        Page: 1,
        PageSize: 25);
}
