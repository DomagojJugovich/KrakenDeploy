using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserCascadeFks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_project_dashboard_views_user_id",
                table: "project_dashboard_views",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_pivot_views_user_id",
                table: "pivot_views",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_dashboard_layouts_user_id",
                table: "dashboard_layouts",
                column: "user_id");

            // Defensive: purge rows whose owning user no longer exists before
            // enforcing the cascade FKs. pivot_views / project_dashboard_views /
            // dashboard_layouts were never cleaned up on user delete, so they
            // can legitimately hold orphans; api_keys / team_members are cleaned
            // by UserService.DeleteAsync but we sweep them too for safety.
            migrationBuilder.Sql("DELETE FROM api_keys WHERE user_id NOT IN (SELECT id FROM users);");
            migrationBuilder.Sql("DELETE FROM team_members WHERE user_id NOT IN (SELECT id FROM users);");
            migrationBuilder.Sql("DELETE FROM pivot_views WHERE user_id NOT IN (SELECT id FROM users);");
            migrationBuilder.Sql("DELETE FROM project_dashboard_views WHERE user_id NOT IN (SELECT id FROM users);");
            migrationBuilder.Sql("DELETE FROM dashboard_layouts WHERE user_id NOT IN (SELECT id FROM users);");

            migrationBuilder.AddForeignKey(
                name: "fk_api_keys_users_user_id",
                table: "api_keys",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_dashboard_layouts_users_user_id",
                table: "dashboard_layouts",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_pivot_views_users_user_id",
                table: "pivot_views",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_project_dashboard_views_users_user_id",
                table: "project_dashboard_views",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_team_members_users_user_id",
                table: "team_members",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_api_keys_users_user_id",
                table: "api_keys");

            migrationBuilder.DropForeignKey(
                name: "fk_dashboard_layouts_users_user_id",
                table: "dashboard_layouts");

            migrationBuilder.DropForeignKey(
                name: "fk_pivot_views_users_user_id",
                table: "pivot_views");

            migrationBuilder.DropForeignKey(
                name: "fk_project_dashboard_views_users_user_id",
                table: "project_dashboard_views");

            migrationBuilder.DropForeignKey(
                name: "fk_team_members_users_user_id",
                table: "team_members");

            migrationBuilder.DropIndex(
                name: "ix_project_dashboard_views_user_id",
                table: "project_dashboard_views");

            migrationBuilder.DropIndex(
                name: "ix_pivot_views_user_id",
                table: "pivot_views");

            migrationBuilder.DropIndex(
                name: "ix_dashboard_layouts_user_id",
                table: "dashboard_layouts");
        }
    }
}
