using Microsoft.EntityFrameworkCore;
using TruckVisit.Domain.Visits;
using Xunit;

namespace TruckVisit.Api.IntegrationTests;

/// <summary>
/// Simulates an attacker who has full database access and asserts that the audit trail gives them
/// away anyway.
/// </summary>
/// <remarks>
/// <para>
/// The other audit tests prove the guards hold. These prove what happens when the guards are
/// removed — which is the scenario that matters, because the guards are only as strong as the
/// account they run under. A DBA, a restore script, or anyone with the owning role can switch a
/// trigger off, change a row and switch it back on. Nothing in the database records that they did.
/// </para>
/// <para>
/// This is the difference between preventing tampering and detecting it, and it is why the chain
/// exists. A regulator does not ask whether the table could have been edited; they ask whether it
/// was.
/// </para>
/// </remarks>
[Collection(PostgresDatabase.Name)]
public sealed class AuditTamperDetectionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task An_untouched_trail_verifies_after_a_round_trip()
    {
        fixture.SkipIfUnavailable();

        var visit = await StoreCompletedVisitAsync();

        await using var context = fixture.CreateContext();
        var reloaded = await LoadAsync(context, visit.Id);

        var verification = reloaded.VerifyAuditTrail();

        // The hashes were computed in memory before the insert and survive the round trip through
        // PostgreSQL unchanged — which is the baseline every assertion below depends on.
        Assert.True(verification.IsIntact);
        Assert.Equal(4, verification.EntriesChecked);
    }

    [Fact]
    public async Task The_trigger_refuses_an_edit_while_it_is_enabled()
    {
        fixture.SkipIfUnavailable();

        var visit = await StoreCompletedVisitAsync();

        await using var context = fixture.CreateContext();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            """UPDATE visit_status_history SET "Reason" = 'tampered' WHERE "VisitId" = {0}""",
            [visit.Id],
            TestContext.Current.CancellationToken));

        Assert.Contains("append-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_edit_made_with_the_trigger_switched_off_is_still_detected()
    {
        fixture.SkipIfUnavailable();

        var visit = await StoreCompletedVisitAsync();

        await using var attacker = fixture.CreateContext();

        // Exactly what someone with the owning role would do, and the reason prevention alone is
        // not a sufficient answer to an auditor.
        await attacker.Database.ExecuteSqlRawAsync(
            "ALTER TABLE visit_status_history DISABLE TRIGGER trg_visit_status_history_append_only;",
            TestContext.Current.CancellationToken);

        await attacker.Database.ExecuteSqlRawAsync(
            """UPDATE visit_status_history SET "Reason" = 'Routine entry.' WHERE "VisitId" = {0} AND "Sequence" = 2""",
            [visit.Id],
            TestContext.Current.CancellationToken);

        await attacker.Database.ExecuteSqlRawAsync(
            "ALTER TABLE visit_status_history ENABLE TRIGGER trg_visit_status_history_append_only;",
            TestContext.Current.CancellationToken);

        // Nothing in the database now shows that anything happened. The row looks ordinary, the
        // trigger is back on, and no log records the change.
        await using var auditor = fixture.CreateContext();
        var reloaded = await LoadAsync(auditor, visit.Id);

        var verification = reloaded.VerifyAuditTrail();

        Assert.False(verification.IsIntact);
        Assert.Equal(2, verification.BrokenAtSequence);
        Assert.Contains("altered", verification.Finding!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_an_entry_with_the_trigger_switched_off_is_still_detected()
    {
        fixture.SkipIfUnavailable();

        var visit = await StoreCompletedVisitAsync();

        await using var attacker = fixture.CreateContext();

        await attacker.Database.ExecuteSqlRawAsync(
            "ALTER TABLE visit_status_history DISABLE TRIGGER trg_visit_status_history_append_only;",
            TestContext.Current.CancellationToken);

        // Removing an inconvenient transition entirely — the tidiest possible cover-up.
        await attacker.Database.ExecuteSqlRawAsync(
            """DELETE FROM visit_status_history WHERE "VisitId" = {0} AND "Sequence" = 3""",
            [visit.Id],
            TestContext.Current.CancellationToken);

        await attacker.Database.ExecuteSqlRawAsync(
            "ALTER TABLE visit_status_history ENABLE TRIGGER trg_visit_status_history_append_only;",
            TestContext.Current.CancellationToken);

        await using var auditor = fixture.CreateContext();
        var reloaded = await LoadAsync(auditor, visit.Id);

        var verification = reloaded.VerifyAuditTrail();

        Assert.False(verification.IsIntact);
        Assert.Equal(3, verification.EntriesChecked);
        Assert.Contains("gap", verification.Finding!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Movement_completion_survives_the_round_trip()
    {
        fixture.SkipIfUnavailable();

        var visit = await StoreCompletedVisitAsync();

        await using var context = fixture.CreateContext();
        var reloaded = await LoadAsync(context, visit.Id);

        var movement = Assert.Single(reloaded.Movements);

        Assert.True(movement.IsCompleted);
        Assert.Equal("yard-crane-1", movement.CompletedBy);
        Assert.False(reloaded.HasOutstandingMovements);
        Assert.Equal("Lovelace Haulage Ltd", reloaded.Driver.CompanyName);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 21, 7, 30, 0, TimeSpan.Zero);

    private static Task<Visit> LoadAsync(Infrastructure.Persistence.TruckVisitDbContext context, Guid id) =>
        context.Visits.AsNoTracking().SingleAsync(
            candidate => candidate.Id == id, TestContext.Current.CancellationToken);

    /// <summary>Stores a visit that has been through the full lifecycle: four audit entries.</summary>
    private async Task<Visit> StoreCompletedVisitAsync()
    {
        var terminal = $"T{Guid.CreateVersion7():N}"[..16].ToUpperInvariant();

        var visit = Visit.Register(
            terminal,
            Truck.Create("MSCU1234567", "34ABC123"),
            Driver.Create("Ada Lovelace", "A1234567", "Lovelace Haulage Ltd", null),
            [Movement.Create(MovementType.Delivery, "MSCU1234567", "YARD1", "BERTH3")],
            "operator-1",
            Now);

        visit.ChangeStatus(VisitStatus.AtGate, "gate-7", Now.AddMinutes(10), "Arrived late.");
        visit.ChangeStatus(VisitStatus.OnSite, "gate-7", Now.AddMinutes(20));
        visit.CompleteMovement(visit.Movements[0].Id, "yard-crane-1", Now.AddMinutes(30));
        visit.ChangeStatus(VisitStatus.Completed, "gate-7", Now.AddMinutes(40));

        await using var context = fixture.CreateContext();
        context.Visits.Add(visit);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return visit;
    }
}
