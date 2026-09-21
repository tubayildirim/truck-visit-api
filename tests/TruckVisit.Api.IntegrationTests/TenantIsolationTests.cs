using Microsoft.EntityFrameworkCore;
using TruckVisit.Domain.Visits;
using TruckVisit.Infrastructure.Persistence;
using Xunit;

namespace TruckVisit.Api.IntegrationTests;

/// <summary>
/// Simulates a connection that has switched into the restricted runtime role and asserts that the
/// database confines it to a terminal scope on its own — independent of whether the application
/// layer's own filtering, or the interceptor that sets this scope for a real request, is even
/// wired correctly.
/// </summary>
/// <remarks>
/// <para>
/// <c>Search_returns_only_the_terminals_it_was_scoped_to</c> in <see cref="VisitPersistenceTests"/>
/// proves the application-layer guard. These tests are the other half, in the same spirit as
/// <see cref="AuditTamperDetectionTests"/>: assume that guard is absent — a query that forgot to
/// filter, a future admin tool, a direct psql session — and prove the database refuses the row
/// anyway.
/// </para>
/// <para>
/// Every test here reproduces by hand exactly what <c>TenantScopeConnectionInterceptor</c> does for
/// a real request: open a connection, tell the session which terminals it may see, then
/// <c>SET ROLE truckvisit_app</c>. Reproducing it rather than exercising the interceptor is
/// deliberate — it keeps the proof about the database's guarantee, not about this codebase's DI
/// wiring, and it means these tests would still catch a regression even if a future change quietly
/// stopped registering the interceptor at all.
/// </para>
/// </remarks>
[Collection(PostgresDatabase.Name)]
public sealed class TenantIsolationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Restricted_role_sees_only_the_terminal_it_was_scoped_to()
    {
        fixture.SkipIfUnavailable();

        var mine = UniqueTerminal();
        var theirs = UniqueTerminal();
        var (mineId, theirsId) = await SeedTwoVisitsAsync(mine, theirs);

        await using var session = await OpenRestrictedSessionAsync(terminals: [mine], globalAccess: false);

        var visible = await session.Context.Visits.Select(visit => visit.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([mineId], visible);
        Assert.DoesNotContain(theirsId, visible);
    }

    [Fact]
    public async Task Restricted_role_with_no_scope_set_sees_nothing_even_though_rows_exist()
    {
        fixture.SkipIfUnavailable();

        var (_, _) = await SeedTwoVisitsAsync(UniqueTerminal(), UniqueTerminal());

        // An empty scope, not an omitted one: this is what a connection looks like the moment
        // after SET ROLE and before the interceptor has told it anything — the state a bug or a
        // half-finished request could plausibly leave behind. The database must default to
        // nothing, not everything.
        await using var session = await OpenRestrictedSessionAsync(terminals: [], globalAccess: false);

        var visible = await session.Context.Visits.CountAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, visible);
    }

    [Fact]
    public async Task Global_access_scope_bypasses_the_terminal_filter()
    {
        fixture.SkipIfUnavailable();

        var (mineId, theirsId) = await SeedTwoVisitsAsync(UniqueTerminal(), UniqueTerminal());

        // What an auditor's token grants: terminals.all, and typically no terminal claim at all —
        // an empty list here on purpose.
        await using var session = await OpenRestrictedSessionAsync(terminals: [], globalAccess: true);

        var visible = await session.Context.Visits.Select(visit => visit.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(mineId, visible);
        Assert.Contains(theirsId, visible);
    }

    [Fact]
    public async Task A_cross_terminal_update_through_the_restricted_role_touches_no_rows()
    {
        fixture.SkipIfUnavailable();

        var mine = UniqueTerminal();
        var theirs = UniqueTerminal();
        var (_, theirsId) = await SeedTwoVisitsAsync(mine, theirs);

        await using (var session = await OpenRestrictedSessionAsync(terminals: [mine], globalAccess: false))
        {
            // The role has UPDATE on visits — this fails for the same reason a lookup would fail:
            // row security makes the row invisible, not the statement illegal. The same choice
            // already made for reads at the API layer (404, never 403 — ARCHITECTURE §7) turns out
            // to be exactly how PostgreSQL behaves here by default: a targeted write against a row
            // outside scope is indistinguishable from a targeted write against a row that does not
            // exist.
            var affected = await session.Context.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE visits SET "CreatedBy" = 'tampered' WHERE "Id" = {theirsId}""",
                TestContext.Current.CancellationToken);

            Assert.Equal(0, affected);
        }

        await using var verify = fixture.CreateContext();
        var stored = await verify.Visits.SingleAsync(
            visit => visit.Id == theirsId, TestContext.Current.CancellationToken);

        Assert.Equal("operator-1", stored.CreatedBy);
    }

    [Fact]
    public async Task The_restricted_role_has_no_update_or_delete_privilege_on_the_audit_trail()
    {
        fixture.SkipIfUnavailable();

        var terminal = UniqueTerminal();
        var (visitId, _) = await SeedTwoVisitsAsync(terminal, terminal);

        // Global access, deliberately: this proves the *privilege* is absent, not merely that row
        // security hid the row. Even a caller who can see everything cannot do this.
        await using var session = await OpenRestrictedSessionAsync(terminals: [], globalAccess: true);

        var update = await Assert.ThrowsAnyAsync<Exception>(() => session.Context.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE visit_status_history SET "Reason" = 'tampered' WHERE "VisitId" = {visitId}""",
            TestContext.Current.CancellationToken));
        Assert.Contains("permission denied", update.Message, StringComparison.OrdinalIgnoreCase);

        var delete = await Assert.ThrowsAnyAsync<Exception>(() => session.Context.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM visit_status_history WHERE "VisitId" = {visitId}""",
            TestContext.Current.CancellationToken));
        Assert.Contains("permission denied", delete.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_restricted_role_cannot_delete_a_visit_or_a_movement()
    {
        fixture.SkipIfUnavailable();

        var terminal = UniqueTerminal();
        var (visitId, _) = await SeedTwoVisitsAsync(terminal, terminal);

        await using var session = await OpenRestrictedSessionAsync(terminals: [], globalAccess: true);

        // Nothing in the application deletes a visit or a movement (ARCHITECTURE §6). That was a
        // convention until this migration; here it is a fact about what the role is capable of.
        var deleteVisit = await Assert.ThrowsAnyAsync<Exception>(() => session.Context.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM visits WHERE "Id" = {visitId}""",
            TestContext.Current.CancellationToken));
        Assert.Contains("permission denied", deleteVisit.Message, StringComparison.OrdinalIgnoreCase);

        var deleteMovement = await Assert.ThrowsAnyAsync<Exception>(() => session.Context.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM visit_movements WHERE "VisitId" = {visitId}""",
            TestContext.Current.CancellationToken));
        Assert.Contains("permission denied", deleteMovement.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 21, 7, 30, 0, TimeSpan.Zero);

    private static string UniqueTerminal() => $"T{Guid.CreateVersion7():N}"[..16].ToUpperInvariant();

    private static Visit NewVisit(string terminalId) => Visit.Register(
        terminalId,
        Truck.Create("MSCU1234567", "34ABC123"),
        Driver.Create("Ada Lovelace", "A1234567", "Lovelace Haulage Ltd", null),
        [Movement.Create(MovementType.Delivery, "MSCU1234567", "YARD1", "BERTH3")],
        "operator-1",
        Now);

    /// <summary>Stores one visit at each of two terminals as the owning role, and returns both ids.</summary>
    private async Task<(Guid MineId, Guid TheirsId)> SeedTwoVisitsAsync(string mine, string theirs)
    {
        var mineVisit = NewVisit(mine);
        var theirsVisit = NewVisit(theirs);

        await using var context = fixture.CreateContext();
        context.Visits.AddRange(mineVisit, theirsVisit);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (mineVisit.Id, theirsVisit.Id);
    }

    /// <summary>
    /// A connection narrowed to <paramref name="terminals"/> exactly the way a real request would
    /// narrow it, kept open for the caller so every statement on it runs under the same session
    /// state — a fresh <see cref="PostgresFixture.CreateContext"/> per statement would otherwise
    /// risk drawing a different physical connection from the pool between them.
    /// </summary>
    private async Task<RestrictedSession> OpenRestrictedSessionAsync(
        IReadOnlyCollection<string> terminals, bool globalAccess)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var context = fixture.CreateContext();

        await context.Database.OpenConnectionAsync(cancellationToken);

        var terminalScope = string.Join(',', terminals);

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.current_terminals', {terminalScope}, false);", cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.terminals_all_scope', {(globalAccess ? "true" : "false")}, false);",
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync("SET ROLE truckvisit_app;", cancellationToken);

        return new RestrictedSession(context);
    }

    /// <summary>
    /// Wraps a restricted context so the physical connection is never returned to the pool still
    /// narrowed to <c>truckvisit_app</c> — the next test to draw it would otherwise silently
    /// inherit a stranger's restrictions, the exact pooling hazard
    /// <c>TenantScopeConnectionInterceptor</c> exists to close off in the running application.
    /// </summary>
    private sealed class RestrictedSession(TruckVisitDbContext context) : IAsyncDisposable
    {
        public TruckVisitDbContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.Database.ExecuteSqlRawAsync("RESET ROLE;", CancellationToken.None);
            await Context.DisposeAsync();
        }
    }
}
