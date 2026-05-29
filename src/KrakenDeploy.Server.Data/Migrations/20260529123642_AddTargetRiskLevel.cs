using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTargetRiskLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "risk_level",
                table: "deployment_targets",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_targets_risk_level",
                table: "deployment_targets",
                column: "risk_level");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deployment_targets_risk_level",
                table: "deployment_targets");

            migrationBuilder.DropColumn(
                name: "risk_level",
                table: "deployment_targets");
        }
    }
}
