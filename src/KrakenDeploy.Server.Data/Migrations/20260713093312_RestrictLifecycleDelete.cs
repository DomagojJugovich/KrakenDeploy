using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestrictLifecycleDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_channels_lifecycles_lifecycle_id",
                table: "channels");

            migrationBuilder.DropForeignKey(
                name: "fk_projects_lifecycles_lifecycle_id",
                table: "projects");

            migrationBuilder.AddForeignKey(
                name: "fk_channels_lifecycles_lifecycle_id",
                table: "channels",
                column: "lifecycle_id",
                principalTable: "lifecycles",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_projects_lifecycles_lifecycle_id",
                table: "projects",
                column: "lifecycle_id",
                principalTable: "lifecycles",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_channels_lifecycles_lifecycle_id",
                table: "channels");

            migrationBuilder.DropForeignKey(
                name: "fk_projects_lifecycles_lifecycle_id",
                table: "projects");

            migrationBuilder.AddForeignKey(
                name: "fk_channels_lifecycles_lifecycle_id",
                table: "channels",
                column: "lifecycle_id",
                principalTable: "lifecycles",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_projects_lifecycles_lifecycle_id",
                table: "projects",
                column: "lifecycle_id",
                principalTable: "lifecycles",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
