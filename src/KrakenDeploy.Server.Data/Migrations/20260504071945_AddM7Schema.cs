using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddM7Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "channel_id",
                table: "releases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lifecycle_id",
                table: "projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "lifecycles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    phases = table.Column<string>(type: "jsonb", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lifecycles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "runbooks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runbooks", x => x.id);
                    table.ForeignKey(
                        name: "fk_runbooks_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    lifecycle_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version_range = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    version_tag = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channels", x => x.id);
                    table.ForeignKey(
                        name: "fk_channels_lifecycles_lifecycle_id",
                        column: x => x.lifecycle_id,
                        principalTable: "lifecycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_channels_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "runbook_processes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    runbook_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runbook_processes", x => x.id);
                    table.ForeignKey(
                        name: "fk_runbook_processes_runbooks_runbook_id",
                        column: x => x.runbook_id,
                        principalTable: "runbooks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "runbook_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    runbook_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    started_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    process_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    next_log_sequence = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runbook_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_runbook_runs_deployment_targets_target_id",
                        column: x => x.target_id,
                        principalTable: "deployment_targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_runbook_runs_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_runbook_runs_runbooks_runbook_id",
                        column: x => x.runbook_id,
                        principalTable: "runbooks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_runbook_runs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "runbook_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    process_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    step_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    target_roles = table.Column<string>(type: "jsonb", nullable: false),
                    package_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runbook_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_runbook_steps_runbook_processes_process_id",
                        column: x => x.process_id,
                        principalTable: "runbook_processes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "runbook_run_log_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    runbook_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runbook_run_log_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_runbook_run_log_entries_runbook_runs_runbook_run_id",
                        column: x => x.runbook_run_id,
                        principalTable: "runbook_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_releases_channel_id",
                table: "releases",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_projects_lifecycle_id",
                table: "projects",
                column: "lifecycle_id");

            migrationBuilder.CreateIndex(
                name: "ix_channels_lifecycle_id",
                table: "channels",
                column: "lifecycle_id");

            migrationBuilder.CreateIndex(
                name: "ix_channels_project_id_name",
                table: "channels",
                columns: new[] { "project_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runbook_processes_runbook_id",
                table: "runbook_processes",
                column: "runbook_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runbook_run_log_entries_runbook_run_id_sequence",
                table: "runbook_run_log_entries",
                columns: new[] { "runbook_run_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runbook_runs_environment_id",
                table: "runbook_runs",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_runs_runbook_id",
                table: "runbook_runs",
                column: "runbook_id");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_runs_status",
                table: "runbook_runs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_runs_target_id",
                table: "runbook_runs",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_runs_tenant_id",
                table: "runbook_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_steps_process_id_sort_order",
                table: "runbook_steps",
                columns: new[] { "process_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_runbooks_project_id_name",
                table: "runbooks",
                columns: new[] { "project_id", "name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_projects_lifecycles_lifecycle_id",
                table: "projects",
                column: "lifecycle_id",
                principalTable: "lifecycles",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_releases_channels_channel_id",
                table: "releases",
                column: "channel_id",
                principalTable: "channels",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_projects_lifecycles_lifecycle_id",
                table: "projects");

            migrationBuilder.DropForeignKey(
                name: "fk_releases_channels_channel_id",
                table: "releases");

            migrationBuilder.DropTable(
                name: "channels");

            migrationBuilder.DropTable(
                name: "runbook_run_log_entries");

            migrationBuilder.DropTable(
                name: "runbook_steps");

            migrationBuilder.DropTable(
                name: "lifecycles");

            migrationBuilder.DropTable(
                name: "runbook_runs");

            migrationBuilder.DropTable(
                name: "runbook_processes");

            migrationBuilder.DropTable(
                name: "runbooks");

            migrationBuilder.DropIndex(
                name: "ix_releases_channel_id",
                table: "releases");

            migrationBuilder.DropIndex(
                name: "ix_projects_lifecycle_id",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "channel_id",
                table: "releases");

            migrationBuilder.DropColumn(
                name: "lifecycle_id",
                table: "projects");
        }
    }
}
