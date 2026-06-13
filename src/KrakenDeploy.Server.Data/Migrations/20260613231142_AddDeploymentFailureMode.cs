using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeploymentFailureMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "failure_mode",
                table: "deployments",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "failure_mode",
                table: "deployments");
        }
    }
}
