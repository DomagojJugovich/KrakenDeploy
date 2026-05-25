using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "performance_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hangfire_worker_count = table.Column<int>(type: "integer", nullable: false),
                    slow_deployment_threshold_minutes = table.Column<int>(type: "integer", nullable: false),
                    slow_step_threshold_minutes = table.Column<int>(type: "integer", nullable: false),
                    audit_log_retention_days = table.Column<int>(type: "integer", nullable: false),
                    ai_call_log_retention_days = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_performance_settings", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "performance_settings");
        }
    }
}
