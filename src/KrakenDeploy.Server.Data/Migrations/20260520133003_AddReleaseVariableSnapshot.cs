using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseVariableSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Empty JSONB array, not empty string — PostgreSQL refuses ''
            // for a jsonb column. Existing release rows get [] which our
            // VariableSnapshotUpdatedUtc IS NULL check correctly identifies
            // as "predates the feature, fall back to live resolution".
            migrationBuilder.AddColumn<string>(
                name: "variable_snapshot",
                table: "releases",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "variable_snapshot_updated_utc",
                table: "releases",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "variable_snapshot",
                table: "releases");

            migrationBuilder.DropColumn(
                name: "variable_snapshot_updated_utc",
                table: "releases");
        }
    }
}
