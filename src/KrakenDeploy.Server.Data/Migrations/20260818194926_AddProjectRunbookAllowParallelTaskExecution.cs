using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectRunbookAllowParallelTaskExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allow_parallel_task_execution",
                table: "runbooks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "allow_parallel_task_execution",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allow_parallel_task_execution",
                table: "runbooks");

            migrationBuilder.DropColumn(
                name: "allow_parallel_task_execution",
                table: "projects");
        }
    }
}
