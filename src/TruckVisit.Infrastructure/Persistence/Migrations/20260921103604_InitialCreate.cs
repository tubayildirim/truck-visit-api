using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TruckVisit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    VisitId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_records", x => new { x.Key, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "visits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TerminalId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    truck_unit_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    truck_license_plate = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    driver_full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    driver_document_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    driver_phone_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    CreatedTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastStatusChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "visit_movements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UnitNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    From = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    To = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VisitId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visit_movements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_visit_movements_visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "visit_status_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    From = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    To = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visit_status_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_visit_status_history_visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_created_at",
                table: "idempotency_records",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_visit_movements_From",
                table: "visit_movements",
                column: "From");

            migrationBuilder.CreateIndex(
                name: "IX_visit_movements_To",
                table: "visit_movements",
                column: "To");

            migrationBuilder.CreateIndex(
                name: "IX_visit_movements_UnitNumber",
                table: "visit_movements",
                column: "UnitNumber");

            migrationBuilder.CreateIndex(
                name: "IX_visit_movements_VisitId",
                table: "visit_movements",
                column: "VisitId");

            migrationBuilder.CreateIndex(
                name: "IX_visit_status_history_VisitId_Sequence",
                table: "visit_status_history",
                columns: new[] { "VisitId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_visits_truck_license_plate",
                table: "visits",
                column: "truck_license_plate");

            migrationBuilder.CreateIndex(
                name: "ix_visits_created_by",
                table: "visits",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "ix_visits_terminal_created",
                table: "visits",
                columns: new[] { "TerminalId", "CreatedTime" });

            migrationBuilder.CreateIndex(
                name: "ix_visits_terminal_status_created",
                table: "visits",
                columns: new[] { "TerminalId", "CurrentStatus", "CreatedTime" });

            // ---------------------------------------------------------------------------------
            // Append-only guarantee, enforced by the database itself.
            //
            // The domain model already makes an in-place edit unreachable, and SaveChanges refuses
            // a modified audit entry. This is the layer that holds for connections which never go
            // through this codebase at all — a DBA session, a reporting tool, a migration script.
            // It is the one an auditor can actually be shown.
            //
            // A trigger rather than REVOKE: REVOKE does not constrain a superuser, and the role
            // owning these tables usually is one. A BEFORE trigger runs for every caller.
            //
            // DELETE is blocked alongside UPDATE. Nothing in the application deletes a visit, and
            // archival works by dropping whole partitions, which is DDL and not a row DELETE — so
            // the retention plan is unaffected.
            // ---------------------------------------------------------------------------------
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION truckvisit_reject_audit_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION
                        'visit_status_history is append-only; % is not permitted on an audit entry', TG_OP
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_visit_status_history_append_only
                BEFORE UPDATE OR DELETE ON visit_status_history
                FOR EACH ROW EXECUTE FUNCTION truckvisit_reject_audit_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the table would take the trigger with it, but not the function it calls.
            // Removed explicitly so a down-migration leaves nothing orphaned behind.
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS trg_visit_status_history_append_only ON visit_status_history;");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS truckvisit_reject_audit_mutation();");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "visit_movements");

            migrationBuilder.DropTable(
                name: "visit_status_history");

            migrationBuilder.DropTable(
                name: "visits");
        }
    }
}
