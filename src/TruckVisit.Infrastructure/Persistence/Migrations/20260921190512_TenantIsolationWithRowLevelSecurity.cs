using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TruckVisit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantIsolationWithRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------------------------------------------------------------------------------
            // A second, independent enforcement of tenant isolation.
            //
            // TerminalId scoping is already enforced in the application layer — CurrentUser
            // resolves it before the repository ever runs, and an empty scope returns an empty
            // page rather than every row (ARCHITECTURE §7). That is one gate, and it only holds
            // for connections that go through this codebase.
            //
            // This is the same argument the append-only trigger already makes for immutability,
            // applied here to authorization: a guarantee that depends on application code
            // remembering to apply a WHERE clause is not a guarantee. A query that forgot to
            // filter, a future admin tool, or a direct psql session against the runtime role must
            // be refused by the database itself, not merely discouraged by convention.
            // ---------------------------------------------------------------------------------

            // A role the running API switches into for the lifetime of a request (see
            // TenantScopeConnectionInterceptor) — never logged into directly. The physical
            // connection always authenticates as the owning role and narrows itself with
            // SET ROLE, because row-level security exempts a table's owner by default and this
            // role is deliberately not the owner.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'truckvisit_app') THEN
                        CREATE ROLE truckvisit_app NOLOGIN;
                    END IF;
                END
                $$;
                """);

            // A superuser could SET ROLE anywhere regardless, but membership is granted
            // explicitly so the guarantee does not quietly depend on the connection being one —
            // it will not be, in production, where the master user is not a true superuser.
            migrationBuilder.Sql("GRANT truckvisit_app TO truckvisit;");

            migrationBuilder.Sql("GRANT USAGE ON SCHEMA public TO truckvisit_app;");

            // Table privileges are a separate layer from row security: GRANT decides which
            // statements are permitted at all, row security decides which rows a permitted
            // statement can reach. Two invariants the application already relies on by
            // convention become real at this layer instead:
            //   * nothing in the application ever deletes a visit or a movement, so DELETE is not
            //     granted on either;
            //   * the audit trail is append-only, so UPDATE and DELETE are withheld entirely on
            //     visit_status_history — the same guarantee the trigger gives, now enforced
            //     twice, for the same reason the trigger was chosen over REVOKE in the first
            //     place: one mechanism is only as strong as the one account that can switch it
            //     off.
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON visits TO truckvisit_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON visit_movements TO truckvisit_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT ON visit_status_history TO truckvisit_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON idempotency_records TO truckvisit_app;");

            // ---------------------------------------------------------------------------------
            // Policies, fail closed by design. current_setting(..., true) returns NULL instead of
            // raising when the session variable was never set — a raw psql session, a migration,
            // a background job — and NULL compared against anything is NULL, not TRUE. A
            // connection that never opts into a terminal scope sees no rows, never every row.
            //
            // FORCE matters even though truckvisit_app is never the table owner: it is the
            // guard against the operational mistake of the owning connection being used directly
            // — a bug, a future tool reusing the migration connection string — without ever
            // switching role. With FORCE, that connection is bound by these policies too, exactly
            // like the append-only trigger binds every caller regardless of role.
            // ---------------------------------------------------------------------------------
            migrationBuilder.Sql("ALTER TABLE visits ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE visits FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY visits_terminal_scope ON visits
                    USING (
                        "TerminalId" = ANY(string_to_array(current_setting('app.current_terminals', true), ','))
                        OR current_setting('app.terminals_all_scope', true) = 'true'
                    );
                """);

            migrationBuilder.Sql("ALTER TABLE visit_movements ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE visit_movements FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY visit_movements_terminal_scope ON visit_movements
                    USING (
                        EXISTS (
                            SELECT 1 FROM visits v
                            WHERE v."Id" = visit_movements."VisitId"
                              AND (
                                  v."TerminalId" = ANY(string_to_array(current_setting('app.current_terminals', true), ','))
                                  OR current_setting('app.terminals_all_scope', true) = 'true'
                              )
                        )
                    );
                """);

            migrationBuilder.Sql("ALTER TABLE visit_status_history ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE visit_status_history FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY visit_status_history_terminal_scope ON visit_status_history
                    USING (
                        EXISTS (
                            SELECT 1 FROM visits v
                            WHERE v."Id" = visit_status_history."VisitId"
                              AND (
                                  v."TerminalId" = ANY(string_to_array(current_setting('app.current_terminals', true), ','))
                                  OR current_setting('app.terminals_all_scope', true) = 'true'
                              )
                        )
                    );
                """);

            // idempotency_records deliberately keeps no policy: a replayed request is deduplicated
            // by (Key, UserId), not by terminal, and the record carries no TerminalId to scope by.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS visit_status_history_terminal_scope ON visit_status_history;");
            migrationBuilder.Sql("ALTER TABLE visit_status_history NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE visit_status_history DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.Sql("DROP POLICY IF EXISTS visit_movements_terminal_scope ON visit_movements;");
            migrationBuilder.Sql("ALTER TABLE visit_movements NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE visit_movements DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.Sql("DROP POLICY IF EXISTS visits_terminal_scope ON visits;");
            migrationBuilder.Sql("ALTER TABLE visits NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE visits DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.Sql("REVOKE ALL ON idempotency_records FROM truckvisit_app;");
            migrationBuilder.Sql("REVOKE ALL ON visit_status_history FROM truckvisit_app;");
            migrationBuilder.Sql("REVOKE ALL ON visit_movements FROM truckvisit_app;");
            migrationBuilder.Sql("REVOKE ALL ON visits FROM truckvisit_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA public FROM truckvisit_app;");
            migrationBuilder.Sql("REVOKE truckvisit_app FROM truckvisit;");
            migrationBuilder.Sql("DROP ROLE IF EXISTS truckvisit_app;");
        }
    }
}
