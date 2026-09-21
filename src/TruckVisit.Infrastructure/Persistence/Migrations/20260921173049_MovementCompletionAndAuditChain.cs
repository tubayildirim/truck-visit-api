using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TruckVisit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MovementCompletionAndAuditChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "driver_company_name",
                table: "visits",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EntryHash",
                table: "visit_status_history",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PreviousHash",
                table: "visit_status_history",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "visit_movements",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletedBy",
                table: "visit_movements",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_visits_driver_company_name",
                table: "visits",
                column: "driver_company_name");

            migrationBuilder.CreateIndex(
                name: "IX_visit_movements_CompletedAt",
                table: "visit_movements",
                column: "CompletedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_visits_driver_company_name",
                table: "visits");

            migrationBuilder.DropIndex(
                name: "IX_visit_movements_CompletedAt",
                table: "visit_movements");

            migrationBuilder.DropColumn(
                name: "driver_company_name",
                table: "visits");

            migrationBuilder.DropColumn(
                name: "EntryHash",
                table: "visit_status_history");

            migrationBuilder.DropColumn(
                name: "PreviousHash",
                table: "visit_status_history");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "visit_movements");

            migrationBuilder.DropColumn(
                name: "CompletedBy",
                table: "visit_movements");
        }
    }
}
