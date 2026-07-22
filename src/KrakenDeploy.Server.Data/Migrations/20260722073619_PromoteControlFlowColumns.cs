using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PromoteControlFlowColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "for_each_collection",
                table: "process_steps",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "for_each_parallel",
                table: "process_steps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "max_parallelism",
                table: "process_steps",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "run_on_server",
                table: "process_steps",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "for_each_collection",
                table: "process_steps");

            migrationBuilder.DropColumn(
                name: "for_each_parallel",
                table: "process_steps");

            migrationBuilder.DropColumn(
                name: "max_parallelism",
                table: "process_steps");

            migrationBuilder.DropColumn(
                name: "run_on_server",
                table: "process_steps");
        }
    }
}
