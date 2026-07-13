using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardenSpaceScopeAndDiagnosisFks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_deployment_diagnoses_space_id",
                table: "deployment_diagnoses",
                column: "space_id");

            migrationBuilder.AddForeignKey(
                name: "fk_adhoc_sessions_spaces_space_id",
                table: "adhoc_sessions",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_ai_call_logs_spaces_space_id",
                table: "ai_call_logs",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dashboard_layouts_spaces_space_id",
                table: "dashboard_layouts",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Defensive: drop any diagnosis whose task no longer exists before
            // enforcing the FK. Diagnoses are normally pinned by the RESTRICT on
            // server_tasks->releases, but a stray row would fail FK creation.
            migrationBuilder.Sql(
                "DELETE FROM deployment_diagnoses WHERE deployment_id NOT IN (SELECT id FROM server_tasks);");

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_diagnoses_server_tasks_deployment_id",
                table: "deployment_diagnoses",
                column: "deployment_id",
                principalTable: "server_tasks",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_diagnoses_spaces_space_id",
                table: "deployment_diagnoses",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_freezes_spaces_space_id",
                table: "deployment_freezes",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_pivot_views_spaces_space_id",
                table: "pivot_views",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_project_dashboard_views_spaces_space_id",
                table: "project_dashboard_views",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_adhoc_sessions_spaces_space_id",
                table: "adhoc_sessions");

            migrationBuilder.DropForeignKey(
                name: "fk_ai_call_logs_spaces_space_id",
                table: "ai_call_logs");

            migrationBuilder.DropForeignKey(
                name: "fk_dashboard_layouts_spaces_space_id",
                table: "dashboard_layouts");

            migrationBuilder.DropForeignKey(
                name: "fk_deployment_diagnoses_server_tasks_deployment_id",
                table: "deployment_diagnoses");

            migrationBuilder.DropForeignKey(
                name: "fk_deployment_diagnoses_spaces_space_id",
                table: "deployment_diagnoses");

            migrationBuilder.DropForeignKey(
                name: "fk_deployment_freezes_spaces_space_id",
                table: "deployment_freezes");

            migrationBuilder.DropForeignKey(
                name: "fk_pivot_views_spaces_space_id",
                table: "pivot_views");

            migrationBuilder.DropForeignKey(
                name: "fk_project_dashboard_views_spaces_space_id",
                table: "project_dashboard_views");

            migrationBuilder.DropIndex(
                name: "ix_deployment_diagnoses_space_id",
                table: "deployment_diagnoses");
        }
    }
}
