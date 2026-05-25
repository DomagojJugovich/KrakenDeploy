using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStepExecutionKnobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "condition",
                table: "deployment_steps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "condition_variable_expression",
                table: "deployment_steps",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_retries",
                table: "deployment_steps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "required",
                table: "deployment_steps",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "retry_delay_seconds",
                table: "deployment_steps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "start_trigger",
                table: "deployment_steps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "timeout_seconds",
                table: "deployment_steps",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "condition",
                table: "deployment_steps");

            migrationBuilder.DropColumn(
                name: "condition_variable_expression",
                table: "deployment_steps");

            migrationBuilder.DropColumn(
                name: "max_retries",
                table: "deployment_steps");

            migrationBuilder.DropColumn(
                name: "required",
                table: "deployment_steps");

            migrationBuilder.DropColumn(
                name: "retry_delay_seconds",
                table: "deployment_steps");

            migrationBuilder.DropColumn(
                name: "start_trigger",
                table: "deployment_steps");

            migrationBuilder.DropColumn(
                name: "timeout_seconds",
                table: "deployment_steps");
        }
    }
}
