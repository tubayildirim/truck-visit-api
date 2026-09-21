using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TruckVisit.Application.Abstractions;

namespace TruckVisit.Infrastructure.Persistence;

/// <summary>
/// Puts the caller's terminal scope into the database session itself, for every connection opened
/// while a real request is being served.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TruckVisitDbContext"/> always authenticates as the role that owns the schema —
/// migrations need that role's DDL privileges, and nothing else in this codebase had a reason to
/// use a different login until now. Row-level security (see the
/// <c>TenantIsolationWithRowLevelSecurity</c> migration) needs the opposite: a role that is
/// provably <em>not</em> the owner, because PostgreSQL exempts an owner from its own row security
/// policies unless the table is explicitly forced. This class is the bridge — every connection
/// opened for an authenticated caller switches into the restricted <c>truckvisit_app</c> role with
/// <c>SET ROLE</c>, and tells the database which terminals that caller may see through two session
/// variables the migration's policies read back: <c>app.current_terminals</c> and
/// <c>app.terminals_all_scope</c>.
/// </para>
/// <para>
/// State is re-established on every open, never assumed to survive from the last one — for two
/// independent reasons, either one sufficient on its own. First, Npgsql pools physical connections,
/// and nothing guarantees a role switch or a session variable from one logical use survives being
/// handed back out for the next. Second, one physical connection legitimately serves different
/// kinds of caller over its lifetime: an authenticated operator, then later the anonymous health
/// check, then perhaps a background scope with no caller at all. Whichever this particular open is
/// for, its state is set fresh here, never inherited from whatever the connection was doing before.
/// </para>
/// <para>
/// Outside a real request — migrations, the development-only auto-migrate at start-up, the
/// anonymous health check — <see cref="ICurrentUser.IsAuthenticated"/> is false, and the connection
/// is reset to the owning role instead. Those callers need schema privileges <c>truckvisit_app</c>
/// was deliberately never granted, and row security does not constrain DDL regardless. One sharp
/// edge follows from <c>FORCE ROW LEVEL SECURITY</c> applying to the owner too: an owner-role
/// connection that queries <c>visits</c> directly (nothing in this codebase does, today) would see
/// zero rows rather than every row, because the session variables are unset and the policy fails
/// closed. Documented here rather than discovered later, the way this repository prefers.
/// </para>
/// </remarks>
internal sealed class TenantScopeConnectionInterceptor(ICurrentUser currentUser) : DbConnectionInterceptor
{
    private const string ResetToOwningRole = "RESET ROLE;";
    private const string SwitchToRestrictedRole = "SET ROLE truckvisit_app;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ApplyAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await ApplyAsync(connection, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            await ExecuteAsync(connection, ResetToOwningRole, cancellationToken).ConfigureAwait(false);
            return;
        }

        // TerminalCode's charset is [A-Z0-9-], which excludes the delimiter — no normalised
        // terminal claim can ever contain a comma, so joining on one can never let a claim
        // masquerade as a second entry.
        var terminals = string.Join(',', currentUser.TerminalIds);
        var bypass = currentUser.HasGlobalTerminalAccess ? "true" : "false";

        await ExecuteAsync(
            connection,
            "SELECT set_config('app.current_terminals', @terminals, false);",
            cancellationToken,
            ("@terminals", terminals)).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            "SELECT set_config('app.terminals_all_scope', @bypass, false);",
            cancellationToken,
            ("@bypass", bypass)).ConfigureAwait(false);

        // Last and unconditional: nothing that runs on this connection after this point should
        // still be doing so as the role that owns the schema.
        await ExecuteAsync(connection, SwitchToRestrictedRole, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;

        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
