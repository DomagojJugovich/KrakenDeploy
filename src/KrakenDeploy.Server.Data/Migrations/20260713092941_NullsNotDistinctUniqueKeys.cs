using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class NullsNotDistinctUniqueKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_teams_space_id_name",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "ix_team_external_groups_team_id_identity_provider_id_group_cla",
                table: "team_external_groups");

            migrationBuilder.DropIndex(
                name: "ix_task_step_outcomes_task_id_step_index_target_id",
                table: "task_step_outcomes");

            migrationBuilder.CreateIndex(
                name: "ix_teams_space_id_name",
                table: "teams",
                columns: new[] { "space_id", "name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_team_external_groups_team_id_identity_provider_id_group_cla",
                table: "team_external_groups",
                columns: new[] { "team_id", "identity_provider_id", "group_claim" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_task_step_outcomes_task_id_step_index_target_id",
                table: "task_step_outcomes",
                columns: new[] { "task_id", "step_index", "target_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_teams_space_id_name",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "ix_team_external_groups_team_id_identity_provider_id_group_cla",
                table: "team_external_groups");

            migrationBuilder.DropIndex(
                name: "ix_task_step_outcomes_task_id_step_index_target_id",
                table: "task_step_outcomes");

            migrationBuilder.CreateIndex(
                name: "ix_teams_space_id_name",
                table: "teams",
                columns: new[] { "space_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_team_external_groups_team_id_identity_provider_id_group_cla",
                table: "team_external_groups",
                columns: new[] { "team_id", "identity_provider_id", "group_claim" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_task_step_outcomes_task_id_step_index_target_id",
                table: "task_step_outcomes",
                columns: new[] { "task_id", "step_index", "target_id" },
                unique: true);
        }
    }
}
