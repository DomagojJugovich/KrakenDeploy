using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backup_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    bundle_path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    bundle_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    triggered_by = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "backup_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_directory = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    schedule_cron = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    schedule_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    retain_last_n = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_backup_runs_started_utc",
                table: "backup_runs",
                column: "started_utc",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_runs");

            migrationBuilder.DropTable(
                name: "backup_settings");
        }
    }
}
