using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStepPackageVersionColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "step_package_name",
                table: "runbook_steps",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "step_package_version",
                table: "runbook_steps",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "step_package_name",
                table: "deployment_steps",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "step_package_version",
                table: "deployment_steps",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "step_package_name",
                table: "runbook_steps");

            migrationBuilder.DropColumn(
                name: "step_package_version",
                table: "runbook_steps");

            migrationBuilder.DropColumn(
                name: "step_package_name",
                table: "deployment_steps");

            migrationBuilder.DropColumn(
                name: "step_package_version",
                table: "deployment_steps");
        }
    }
}
