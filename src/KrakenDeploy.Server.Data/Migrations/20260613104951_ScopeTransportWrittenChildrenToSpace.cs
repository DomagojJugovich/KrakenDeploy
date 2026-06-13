using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeTransportWrittenChildrenToSpace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "runbook_runs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "runbook_run_log_entries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "deployment_step_outcomes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "deployment_output_variables",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "deployment_log_entries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "deployment_artifacts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "adhoc_iterations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill space_id from each row's Space-scoped parent BEFORE the
            // Restrict FK to spaces is added (Guid.Empty default isn't a valid
            // Space). runbook_runs first, then its log entries inherit from it.
            migrationBuilder.Sql(
                "UPDATE runbook_runs rr SET space_id = r.space_id " +
                "FROM runbooks r WHERE rr.runbook_id = r.id;");
            migrationBuilder.Sql(
                "UPDATE runbook_run_log_entries l SET space_id = rr.space_id " +
                "FROM runbook_runs rr WHERE l.runbook_run_id = rr.id;");
            migrationBuilder.Sql(
                "UPDATE deployment_step_outcomes o SET space_id = d.space_id " +
                "FROM deployments d WHERE o.deployment_id = d.id;");
            migrationBuilder.Sql(
                "UPDATE deployment_output_variables ov SET space_id = d.space_id " +
                "FROM deployments d WHERE ov.deployment_id = d.id;");
            migrationBuilder.Sql(
                "UPDATE deployment_log_entries le SET space_id = d.space_id " +
                "FROM deployments d WHERE le.deployment_id = d.id;");
            migrationBuilder.Sql(
                "UPDATE deployment_artifacts a SET space_id = d.space_id " +
                "FROM deployments d WHERE a.deployment_id = d.id;");
            migrationBuilder.Sql(
                "UPDATE adhoc_iterations i SET space_id = s.space_id " +
                "FROM adhoc_sessions s WHERE i.session_id = s.id;");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_runs_space_id",
                table: "runbook_runs",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_run_log_entries_space_id",
                table: "runbook_run_log_entries",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_step_outcomes_space_id",
                table: "deployment_step_outcomes",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_output_variables_space_id",
                table: "deployment_output_variables",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_log_entries_space_id",
                table: "deployment_log_entries",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_artifacts_space_id",
                table: "deployment_artifacts",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_adhoc_iterations_space_id",
                table: "adhoc_iterations",
                column: "space_id");

            migrationBuilder.AddForeignKey(
                name: "fk_adhoc_iterations_spaces_space_id",
                table: "adhoc_iterations",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_artifacts_spaces_space_id",
                table: "deployment_artifacts",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_log_entries_spaces_space_id",
                table: "deployment_log_entries",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_output_variables_spaces_space_id",
                table: "deployment_output_variables",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_step_outcomes_spaces_space_id",
                table: "deployment_step_outcomes",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_runbook_run_log_entries_spaces_space_id",
                table: "runbook_run_log_entries",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_runbook_runs_spaces_space_id",
                table: "runbook_runs",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_adhoc_iterations_spaces_space_id",
                table: "adhoc_iterations");

            migrationBuilder.DropForeignKey(
                name: "fk_deployment_artifacts_spaces_space_id",
                table: "deployment_artifacts");

            migrationBuilder.DropForeignKey(
                name: "fk_deployment_log_entries_spaces_space_id",
                table: "deployment_log_entries");

            migrationBuilder.DropForeignKey(
                name: "fk_deployment_output_variables_spaces_space_id",
                table: "deployment_output_variables");

            migrationBuilder.DropForeignKey(
                name: "fk_deployment_step_outcomes_spaces_space_id",
                table: "deployment_step_outcomes");

            migrationBuilder.DropForeignKey(
                name: "fk_runbook_run_log_entries_spaces_space_id",
                table: "runbook_run_log_entries");

            migrationBuilder.DropForeignKey(
                name: "fk_runbook_runs_spaces_space_id",
                table: "runbook_runs");

            migrationBuilder.DropIndex(
                name: "ix_runbook_runs_space_id",
                table: "runbook_runs");

            migrationBuilder.DropIndex(
                name: "ix_runbook_run_log_entries_space_id",
                table: "runbook_run_log_entries");

            migrationBuilder.DropIndex(
                name: "ix_deployment_step_outcomes_space_id",
                table: "deployment_step_outcomes");

            migrationBuilder.DropIndex(
                name: "ix_deployment_output_variables_space_id",
                table: "deployment_output_variables");

            migrationBuilder.DropIndex(
                name: "ix_deployment_log_entries_space_id",
                table: "deployment_log_entries");

            migrationBuilder.DropIndex(
                name: "ix_deployment_artifacts_space_id",
                table: "deployment_artifacts");

            migrationBuilder.DropIndex(
                name: "ix_adhoc_iterations_space_id",
                table: "adhoc_iterations");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "runbook_runs");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "runbook_run_log_entries");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "deployment_step_outcomes");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "deployment_output_variables");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "deployment_log_entries");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "deployment_artifacts");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "adhoc_iterations");
        }
    }
}
