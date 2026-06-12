using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeSavedViewsToSpace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_project_dashboard_views_user_id",
                table: "project_dashboard_views");

            migrationBuilder.DropIndex(
                name: "ix_pivot_views_user_id_name",
                table: "pivot_views");

            // Backfill default hand-set to WellKnown.DefaultSpaceId — rows saved
            // before space-scoping all belong to the bootstrap Default Space;
            // Guid.Empty would orphan them behind the global query filter.
            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "project_dashboard_views",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000d543"));

            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "pivot_views",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-00000000d543"));

            migrationBuilder.CreateIndex(
                name: "ix_project_dashboard_views_space_id_user_id",
                table: "project_dashboard_views",
                columns: new[] { "space_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pivot_views_space_id_user_id_name",
                table: "pivot_views",
                columns: new[] { "space_id", "user_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_project_dashboard_views_space_id_user_id",
                table: "project_dashboard_views");

            migrationBuilder.DropIndex(
                name: "ix_pivot_views_space_id_user_id_name",
                table: "pivot_views");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "project_dashboard_views");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "pivot_views");

            migrationBuilder.CreateIndex(
                name: "ix_project_dashboard_views_user_id",
                table: "project_dashboard_views",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pivot_views_user_id_name",
                table: "pivot_views",
                columns: new[] { "user_id", "name" },
                unique: true);
        }
    }
}
