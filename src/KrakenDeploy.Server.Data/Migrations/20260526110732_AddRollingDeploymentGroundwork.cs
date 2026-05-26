using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRollingDeploymentGroundwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deployment_step_outcomes_deployment_id_step_index",
                table: "deployment_step_outcomes");

            migrationBuilder.AddColumn<Guid>(
                name: "target_id",
                table: "deployment_step_outcomes",
                type: "uuid",
                nullable: true);

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

            migrationBuilder.CreateIndex(
                name: "ix_deployment_step_outcomes_deployment_id_step_index_target_id",
                table: "deployment_step_outcomes",
                columns: new[] { "deployment_id", "step_index", "target_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_target_assignments_target_id",
                table: "deployment_target_assignments",
                column: "target_id");

            // ── M-RollingDeployments groundwork — backfill ────────────────
            // 1. For every existing deployment with a target, insert one
            //    join row so the new code path can read the target set
            //    from the join without falling back to the legacy
            //    Deployment.TargetId column. Single-target deployments
            //    behave identically through the join.
            migrationBuilder.Sql("""
                INSERT INTO deployment_target_assignments
                    (deployment_id, target_id, added_utc)
                SELECT id, target_id, COALESCE(started_utc, created_utc, NOW())
                FROM deployments
                WHERE target_id IS NOT NULL;
                """);

            // 2. For every existing DeploymentStepOutcome row, backfill
            //    target_id from the parent deployment's TargetId so the
            //    Steps tab's per-target view (when it lands) shows
            //    historical outcomes against the right target rather
            //    than as orphans. NULL deployment.TargetId rows
            //    (e.g. offline-drop with no machine) stay NULL — those
            //    outcomes weren't bound to a specific target anyway.
            migrationBuilder.Sql("""
                UPDATE deployment_step_outcomes o
                SET target_id = d.target_id
                FROM deployments d
                WHERE o.deployment_id = d.id
                  AND d.target_id IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deployment_target_assignments");

            migrationBuilder.DropIndex(
                name: "ix_deployment_step_outcomes_deployment_id_step_index_target_id",
                table: "deployment_step_outcomes");

            migrationBuilder.DropColumn(
                name: "target_id",
                table: "deployment_step_outcomes");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_step_outcomes_deployment_id_step_index",
                table: "deployment_step_outcomes",
                columns: new[] { "deployment_id", "step_index" },
                unique: true);
        }
    }
}
