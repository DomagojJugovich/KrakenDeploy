using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropDeploymentTargetIdAndRestrictTargetDeletes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Safety-net backfill before dropping the transitional column:
            // the historical backfill created one assignment row per
            // deployments.target_id, but the old dispatch path tolerated a
            // hand-deleted join row by falling back to the column — re-seed
            // any such stragglers so no deployment loses its target set.
            // (Idempotent; skips targets that no longer exist — the old FK
            // was SET NULL, so the column can't dangle, but belt-and-braces.)
            migrationBuilder.Sql("""
                INSERT INTO deployment_target_assignments (deployment_id, target_id, added_utc)
                SELECT d.id, d.target_id, now()
                FROM deployments d
                WHERE d.target_id IS NOT NULL
                  AND EXISTS (SELECT 1 FROM deployment_targets t WHERE t.id = d.target_id)
                  AND NOT EXISTS (
                      SELECT 1 FROM deployment_target_assignments a
                      WHERE a.deployment_id = d.id AND a.target_id = d.target_id);
                """);

            // deployment_step_outcomes.target_id was a bare column (no FK) and
            // can dangle after historical target deletes — null those out so
            // the new RESTRICT FK below can be created.
            migrationBuilder.Sql("""
                UPDATE deployment_step_outcomes o
                SET target_id = NULL
                WHERE o.target_id IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM deployment_targets t WHERE t.id = o.target_id);
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_deployments_deployment_targets_target_id",
                table: "deployments");

            migrationBuilder.DropForeignKey(
                name: "fk_runbook_runs_deployment_targets_target_id",
                table: "runbook_runs");

            migrationBuilder.DropIndex(
                name: "ix_deployments_release_id_environment_id_target_id",
                table: "deployments");

            migrationBuilder.DropIndex(
                name: "ix_deployments_target_id",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "target_id",
                table: "deployments");

            migrationBuilder.CreateIndex(
                name: "ix_deployments_release_id_environment_id",
                table: "deployments",
                columns: new[] { "release_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_step_outcomes_target_id",
                table: "deployment_step_outcomes",
                column: "target_id");

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_step_outcomes_deployment_targets_target_id",
                table: "deployment_step_outcomes",
                column: "target_id",
                principalTable: "deployment_targets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_runbook_runs_deployment_targets_target_id",
                table: "runbook_runs",
                column: "target_id",
                principalTable: "deployment_targets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_deployment_step_outcomes_deployment_targets_target_id",
                table: "deployment_step_outcomes");

            migrationBuilder.DropForeignKey(
                name: "fk_runbook_runs_deployment_targets_target_id",
                table: "runbook_runs");

            migrationBuilder.DropIndex(
                name: "ix_deployments_release_id_environment_id",
                table: "deployments");

            migrationBuilder.DropIndex(
                name: "ix_deployment_step_outcomes_target_id",
                table: "deployment_step_outcomes");

            migrationBuilder.AddColumn<Guid>(
                name: "target_id",
                table: "deployments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployments_release_id_environment_id_target_id",
                table: "deployments",
                columns: new[] { "release_id", "environment_id", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_deployments_target_id",
                table: "deployments",
                column: "target_id");

            migrationBuilder.AddForeignKey(
                name: "fk_deployments_deployment_targets_target_id",
                table: "deployments",
                column: "target_id",
                principalTable: "deployment_targets",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_runbook_runs_deployment_targets_target_id",
                table: "runbook_runs",
                column: "target_id",
                principalTable: "deployment_targets",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
