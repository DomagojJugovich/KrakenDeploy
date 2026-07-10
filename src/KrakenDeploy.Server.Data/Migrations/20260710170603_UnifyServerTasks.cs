using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnifyServerTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_runbook_runs_deployment_targets_target_id",
                table: "runbook_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_runbook_runs_environments_environment_id",
                table: "runbook_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_runbook_runs_runbooks_runbook_id",
                table: "runbook_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_runbook_runs_spaces_space_id",
                table: "runbook_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_runbook_runs_tenants_tenant_id",
                table: "runbook_runs");

            migrationBuilder.DropTable(
                name: "deployment_artifacts");

            migrationBuilder.DropTable(
                name: "deployment_log_entries");

            migrationBuilder.DropTable(
                name: "deployment_output_variables");

            migrationBuilder.DropTable(
                name: "deployment_step_outcomes");

            migrationBuilder.DropTable(
                name: "deployment_steps");

            migrationBuilder.DropTable(
                name: "deployment_target_assignments");

            migrationBuilder.DropTable(
                name: "runbook_run_log_entries");

            migrationBuilder.DropTable(
                name: "runbook_steps");

            migrationBuilder.DropTable(
                name: "deployment_processes");

            migrationBuilder.DropTable(
                name: "deployments");

            migrationBuilder.DropTable(
                name: "runbook_processes");

            // Destructive unification (pre-release, no data preservation): EF reuses
            // the old runbook_runs table as the server_tasks TPH base below —
            // renaming target_id -> release_id and adding kind/project_id. Clear any
            // existing rows first so the rename can't leave mislabeled (kind=0,
            // release_id-from-target) rows that would violate ck_server_tasks_kind_owner
            // on a populated database. A no-op on the empty/fresh DBs migrations run against.
            migrationBuilder.Sql("DELETE FROM runbook_runs;");

            migrationBuilder.DropPrimaryKey(
                name: "pk_runbook_runs",
                table: "runbook_runs");

            migrationBuilder.DropIndex(
                name: "ix_runbook_runs_target_id",
                table: "runbook_runs");

            migrationBuilder.RenameTable(
                name: "runbook_runs",
                newName: "server_tasks");

            migrationBuilder.RenameColumn(
                name: "target_id",
                table: "server_tasks",
                newName: "release_id");

            migrationBuilder.RenameIndex(
                name: "ix_runbook_runs_tenant_id",
                table: "server_tasks",
                newName: "ix_server_tasks_tenant_id");

            migrationBuilder.RenameIndex(
                name: "ix_runbook_runs_status",
                table: "server_tasks",
                newName: "ix_server_tasks_status");

            migrationBuilder.RenameIndex(
                name: "ix_runbook_runs_space_id",
                table: "server_tasks",
                newName: "ix_server_tasks_space_id");

            migrationBuilder.RenameIndex(
                name: "ix_runbook_runs_runbook_id",
                table: "server_tasks",
                newName: "ix_server_tasks_runbook_id");

            migrationBuilder.RenameIndex(
                name: "ix_runbook_runs_environment_id",
                table: "server_tasks",
                newName: "ix_server_tasks_environment_id");

            migrationBuilder.AlterColumn<Guid>(
                name: "runbook_id",
                table: "server_tasks",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "process_snapshot",
                table: "server_tasks",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.AddColumn<Guid>(
                name: "channel_id",
                table: "server_tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "drop_bundle_path",
                table: "server_tasks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "failure_mode",
                table: "server_tasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "form_values",
                table: "server_tasks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "kind",
                table: "server_tasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_task_id",
                table: "server_tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "server_tasks",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "scheduled_for",
                table: "server_tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_server_tasks",
                table: "server_tasks",
                column: "id");

            migrationBuilder.CreateTable(
                name: "processes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_kind = table.Column<int>(type: "integer", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processes", x => x.id);
                    table.ForeignKey(
                        name: "fk_processes_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "task_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    file_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    content_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    stored_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    collected_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "fk_task_artifacts_server_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "server_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_task_artifacts_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "task_log_live",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_index = table.Column<int>(type: "integer", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_log_live", x => x.id);
                    table.ForeignKey(
                        name: "fk_task_log_live_server_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "server_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_output_variables",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    captured_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_output_variables", x => x.id);
                    table.ForeignKey(
                        name: "fk_task_output_variables_server_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "server_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_task_output_variables_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "task_step_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_index = table.Column<int>(type: "integer", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    error_count = table.Column<int>(type: "integer", nullable: false),
                    warn_count = table.Column<int>(type: "integer", nullable: false),
                    first_error_line = table.Column<int>(type: "integer", nullable: true),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    completed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_step_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_task_step_logs_server_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "server_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_step_outcomes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_index = table.Column<int>(type: "integer", nullable: false),
                    step_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    started_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_server_side = table.Column<bool>(type: "boolean", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_step_outcomes", x => x.id);
                    table.ForeignKey(
                        name: "fk_task_step_outcomes_deployment_targets_target_id",
                        column: x => x.target_id,
                        principalTable: "deployment_targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_task_step_outcomes_server_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "server_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_task_step_outcomes_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "task_target_assignments",
                columns: table => new
                {
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_target_assignments", x => new { x.task_id, x.target_id });
                    table.ForeignKey(
                        name: "fk_task_target_assignments_deployment_targets_target_id",
                        column: x => x.target_id,
                        principalTable: "deployment_targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_task_target_assignments_server_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "server_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "process_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    process_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    step_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    target_roles = table.Column<List<string>>(type: "text[]", nullable: false),
                    package_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    step_package_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    step_package_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    condition = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    condition_variable_expression = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    required = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    max_retries = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    retry_delay_seconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    start_trigger = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    parent_step_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_process_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_process_steps_process_steps_parent_step_id",
                        column: x => x.parent_step_id,
                        principalTable: "process_steps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_process_steps_processes_process_id",
                        column: x => x.process_id,
                        principalTable: "processes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_process_steps_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_parent_task_id",
                table: "server_tasks",
                column: "parent_task_id",
                filter: "parent_task_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_project_id",
                table: "server_tasks",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_release_id_environment_id",
                table: "server_tasks",
                columns: new[] { "release_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_scheduled_for",
                table: "server_tasks",
                column: "scheduled_for",
                filter: "scheduled_for IS NOT NULL AND status = 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_server_tasks_kind_owner",
                table: "server_tasks",
                sql: "(kind = 0 AND release_id IS NOT NULL AND runbook_id IS NULL) OR (kind = 1 AND runbook_id IS NOT NULL AND release_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_process_steps_parent_step_id",
                table: "process_steps",
                column: "parent_step_id",
                filter: "parent_step_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_process_steps_process_id_sort_order",
                table: "process_steps",
                columns: new[] { "process_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_process_steps_space_id",
                table: "process_steps",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_processes_owner_kind_owner_id",
                table: "processes",
                columns: new[] { "owner_kind", "owner_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_processes_space_id",
                table: "processes",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_artifacts_space_id",
                table: "task_artifacts",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_artifacts_task_id",
                table: "task_artifacts",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_log_live_task_id_sequence",
                table: "task_log_live",
                columns: new[] { "task_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_task_output_variables_space_id",
                table: "task_output_variables",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_output_variables_task_id_step_name",
                table: "task_output_variables",
                columns: new[] { "task_id", "step_name" });

            migrationBuilder.CreateIndex(
                name: "ix_task_output_variables_task_id_step_name_name",
                table: "task_output_variables",
                columns: new[] { "task_id", "step_name", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_task_step_logs_task_id_step_index_target_id",
                table: "task_step_logs",
                columns: new[] { "task_id", "step_index", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_task_step_outcomes_space_id",
                table: "task_step_outcomes",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_step_outcomes_target_id",
                table: "task_step_outcomes",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_step_outcomes_task_id_step_index_target_id",
                table: "task_step_outcomes",
                columns: new[] { "task_id", "step_index", "target_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_task_target_assignments_target_id",
                table: "task_target_assignments",
                column: "target_id");

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_environments_environment_id",
                table: "server_tasks",
                column: "environment_id",
                principalTable: "environments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_releases_release_id",
                table: "server_tasks",
                column: "release_id",
                principalTable: "releases",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_runbooks_runbook_id",
                table: "server_tasks",
                column: "runbook_id",
                principalTable: "runbooks",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_server_tasks_parent_task_id",
                table: "server_tasks",
                column: "parent_task_id",
                principalTable: "server_tasks",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_spaces_space_id",
                table: "server_tasks",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_tenants_tenant_id",
                table: "server_tasks",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_environments_environment_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_releases_release_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_runbooks_runbook_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_server_tasks_parent_task_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_spaces_space_id",
                table: "server_tasks");

            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_tenants_tenant_id",
                table: "server_tasks");

            migrationBuilder.DropTable(
                name: "process_steps");

            migrationBuilder.DropTable(
                name: "task_artifacts");

            migrationBuilder.DropTable(
                name: "task_log_live");

            migrationBuilder.DropTable(
                name: "task_output_variables");

            migrationBuilder.DropTable(
                name: "task_step_logs");

            migrationBuilder.DropTable(
                name: "task_step_outcomes");

            migrationBuilder.DropTable(
                name: "task_target_assignments");

            migrationBuilder.DropTable(
                name: "processes");

            migrationBuilder.DropPrimaryKey(
                name: "pk_server_tasks",
                table: "server_tasks");

            migrationBuilder.DropIndex(
                name: "ix_server_tasks_parent_task_id",
                table: "server_tasks");

            migrationBuilder.DropIndex(
                name: "ix_server_tasks_project_id",
                table: "server_tasks");

            migrationBuilder.DropIndex(
                name: "ix_server_tasks_release_id_environment_id",
                table: "server_tasks");

            migrationBuilder.DropIndex(
                name: "ix_server_tasks_scheduled_for",
                table: "server_tasks");

            migrationBuilder.DropCheckConstraint(
                name: "ck_server_tasks_kind_owner",
                table: "server_tasks");

            migrationBuilder.DropColumn(
                name: "channel_id",
                table: "server_tasks");

            migrationBuilder.DropColumn(
                name: "drop_bundle_path",
                table: "server_tasks");

            migrationBuilder.DropColumn(
                name: "failure_mode",
                table: "server_tasks");

            migrationBuilder.DropColumn(
                name: "form_values",
                table: "server_tasks");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "server_tasks");

            migrationBuilder.DropColumn(
                name: "parent_task_id",
                table: "server_tasks");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "server_tasks");

            migrationBuilder.DropColumn(
                name: "scheduled_for",
                table: "server_tasks");

            migrationBuilder.RenameTable(
                name: "server_tasks",
                newName: "runbook_runs");

            migrationBuilder.RenameColumn(
                name: "release_id",
                table: "runbook_runs",
                newName: "target_id");

            migrationBuilder.RenameIndex(
                name: "ix_server_tasks_tenant_id",
                table: "runbook_runs",
                newName: "ix_runbook_runs_tenant_id");

            migrationBuilder.RenameIndex(
                name: "ix_server_tasks_status",
                table: "runbook_runs",
                newName: "ix_runbook_runs_status");

            migrationBuilder.RenameIndex(
                name: "ix_server_tasks_space_id",
                table: "runbook_runs",
                newName: "ix_runbook_runs_space_id");

            migrationBuilder.RenameIndex(
                name: "ix_server_tasks_runbook_id",
                table: "runbook_runs",
                newName: "ix_runbook_runs_runbook_id");

            migrationBuilder.RenameIndex(
                name: "ix_server_tasks_environment_id",
                table: "runbook_runs",
                newName: "ix_runbook_runs_environment_id");

            migrationBuilder.AlterColumn<Guid>(
                name: "runbook_id",
                table: "runbook_runs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "process_snapshot",
                table: "runbook_runs",
                type: "jsonb",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_runbook_runs",
                table: "runbook_runs",
                column: "id");

            migrationBuilder.CreateTable(
                name: "deployment_processes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_processes", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_processes_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deployment_processes_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    drop_bundle_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    failure_mode = table.Column<int>(type: "integer", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_log_sequence = table.Column<int>(type: "integer", nullable: false),
                    scheduled_for = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployments", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployments_deployments_parent_deployment_id",
                        column: x => x.parent_deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_deployments_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_deployments_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_deployments_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_deployments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "runbook_processes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    runbook_id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false)
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
                    table.ForeignKey(
                        name: "fk_runbook_processes_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "runbook_run_log_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    runbook_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                    table.ForeignKey(
                        name: "fk_runbook_run_log_entries_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployment_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_step_id = table.Column<Guid>(type: "uuid", nullable: true),
                    process_id = table.Column<Guid>(type: "uuid", nullable: false),
                    condition = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    condition_variable_expression = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    max_retries = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    package_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    retry_delay_seconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_trigger = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    step_package_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    step_package_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    step_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_roles = table.Column<List<string>>(type: "text[]", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_steps_deployment_processes_process_id",
                        column: x => x.process_id,
                        principalTable: "deployment_processes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deployment_steps_deployment_steps_parent_step_id",
                        column: x => x.parent_step_id,
                        principalTable: "deployment_steps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deployment_steps_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployment_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collected_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    file_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    stored_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_artifacts_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deployment_artifacts_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployment_log_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_log_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_log_entries_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deployment_log_entries_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployment_output_variables",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    captured_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_output_variables", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_output_variables_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deployment_output_variables_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployment_step_outcomes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    completed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    is_server_side = table.Column<bool>(type: "boolean", nullable: false),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    step_index = table.Column<int>(type: "integer", nullable: false),
                    step_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_step_outcomes", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_step_outcomes_deployment_targets_target_id",
                        column: x => x.target_id,
                        principalTable: "deployment_targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_deployment_step_outcomes_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_deployment_step_outcomes_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployment_target_assignments",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_target_assignments", x => new { x.deployment_id, x.target_id });
                    table.ForeignKey(
                        name: "fk_deployment_target_assignments_deployment_targets_target_id",
                        column: x => x.target_id,
                        principalTable: "deployment_targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_deployment_target_assignments_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "runbook_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_step_id = table.Column<Guid>(type: "uuid", nullable: true),
                    process_id = table.Column<Guid>(type: "uuid", nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    package_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_package_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    step_package_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    step_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    target_roles = table.Column<string>(type: "jsonb", nullable: false)
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
                    table.ForeignKey(
                        name: "fk_runbook_steps_runbook_steps_parent_step_id",
                        column: x => x.parent_step_id,
                        principalTable: "runbook_steps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_runbook_steps_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_runbook_runs_target_id",
                table: "runbook_runs",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_artifacts_deployment_id",
                table: "deployment_artifacts",
                column: "deployment_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_artifacts_space_id",
                table: "deployment_artifacts",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_log_entries_deployment_id_sequence",
                table: "deployment_log_entries",
                columns: new[] { "deployment_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_log_entries_space_id",
                table: "deployment_log_entries",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_output_variables_deployment_id_step_name",
                table: "deployment_output_variables",
                columns: new[] { "deployment_id", "step_name" });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_output_variables_deployment_id_step_name_name",
                table: "deployment_output_variables",
                columns: new[] { "deployment_id", "step_name", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_output_variables_space_id",
                table: "deployment_output_variables",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_processes_project_id",
                table: "deployment_processes",
                column: "project_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_processes_space_id",
                table: "deployment_processes",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_step_outcomes_deployment_id_step_index_target_id",
                table: "deployment_step_outcomes",
                columns: new[] { "deployment_id", "step_index", "target_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_step_outcomes_space_id",
                table: "deployment_step_outcomes",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_step_outcomes_target_id",
                table: "deployment_step_outcomes",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_steps_parent_step_id",
                table: "deployment_steps",
                column: "parent_step_id",
                filter: "parent_step_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_steps_process_id_sort_order",
                table: "deployment_steps",
                columns: new[] { "process_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_steps_space_id",
                table: "deployment_steps",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_target_assignments_target_id",
                table: "deployment_target_assignments",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_environment_id",
                table: "deployments",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_parent_deployment_id",
                table: "deployments",
                column: "parent_deployment_id",
                filter: "parent_deployment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_release_id_environment_id",
                table: "deployments",
                columns: new[] { "release_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_deployments_scheduled_for",
                table: "deployments",
                column: "scheduled_for",
                filter: "scheduled_for IS NOT NULL AND status = 0");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_space_id",
                table: "deployments",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_status",
                table: "deployments",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_tenant_id",
                table: "deployments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_processes_runbook_id",
                table: "runbook_processes",
                column: "runbook_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runbook_processes_space_id",
                table: "runbook_processes",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_run_log_entries_runbook_run_id_sequence",
                table: "runbook_run_log_entries",
                columns: new[] { "runbook_run_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runbook_run_log_entries_space_id",
                table: "runbook_run_log_entries",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_steps_parent_step_id",
                table: "runbook_steps",
                column: "parent_step_id",
                filter: "parent_step_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_steps_process_id_sort_order",
                table: "runbook_steps",
                columns: new[] { "process_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_runbook_steps_space_id",
                table: "runbook_steps",
                column: "space_id");

            migrationBuilder.AddForeignKey(
                name: "fk_runbook_runs_deployment_targets_target_id",
                table: "runbook_runs",
                column: "target_id",
                principalTable: "deployment_targets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_runbook_runs_environments_environment_id",
                table: "runbook_runs",
                column: "environment_id",
                principalTable: "environments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_runbook_runs_runbooks_runbook_id",
                table: "runbook_runs",
                column: "runbook_id",
                principalTable: "runbooks",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_runbook_runs_spaces_space_id",
                table: "runbook_runs",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_runbook_runs_tenants_tenant_id",
                table: "runbook_runs",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
