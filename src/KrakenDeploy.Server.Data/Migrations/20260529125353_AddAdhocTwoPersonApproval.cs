using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdhocTwoPersonApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "adhoc_two_person_approval",
                table: "space_ai_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_approved_at_utc",
                table: "adhoc_iterations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "first_approved_by_display",
                table: "adhoc_iterations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "first_approved_by_user_id",
                table: "adhoc_iterations",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "adhoc_two_person_approval",
                table: "space_ai_settings");

            migrationBuilder.DropColumn(
                name: "first_approved_at_utc",
                table: "adhoc_iterations");

            migrationBuilder.DropColumn(
                name: "first_approved_by_display",
                table: "adhoc_iterations");

            migrationBuilder.DropColumn(
                name: "first_approved_by_user_id",
                table: "adhoc_iterations");
        }
    }
}
