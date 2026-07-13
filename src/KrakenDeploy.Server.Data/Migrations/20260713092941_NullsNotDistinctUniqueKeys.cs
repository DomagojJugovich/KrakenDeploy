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

            // Defensive: the old NULLS-DISTINCT indexes permitted rows that
            // collide once NULLs are collapsed. Dedupe (keep the lowest id per
            // NULL-collapsed key) BEFORE recreating the indexes, or the unique
            // index creation aborts on a populated DB. Matches the sibling
            // migrations in this wave.
            migrationBuilder.Sql(@"
                DELETE FROM teams t USING teams keep
                WHERE t.id > keep.id AND t.name = keep.name
                  AND (t.space_id = keep.space_id OR (t.space_id IS NULL AND keep.space_id IS NULL));");
            migrationBuilder.Sql(@"
                DELETE FROM team_external_groups t USING team_external_groups keep
                WHERE t.id > keep.id AND t.team_id = keep.team_id AND t.group_claim = keep.group_claim
                  AND (t.identity_provider_id = keep.identity_provider_id
                       OR (t.identity_provider_id IS NULL AND keep.identity_provider_id IS NULL));");
            migrationBuilder.Sql(@"
                DELETE FROM task_step_outcomes t USING task_step_outcomes keep
                WHERE t.id > keep.id AND t.task_id = keep.task_id AND t.step_index = keep.step_index
                  AND (t.target_id = keep.target_id
                       OR (t.target_id IS NULL AND keep.target_id IS NULL));");

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
