using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskLogCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // E-D: move the log-sequence counter off server_tasks (where every
            // append churned the row's xmin, the B5 concurrency token) into a
            // one-row-per-task table allocated by an atomic upsert.
            migrationBuilder.CreateTable(
                name: "task_log_counters",
                columns: table => new
                {
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_sequence = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_log_counters", x => x.task_id);
                    table.ForeignKey(
                        name: "fk_task_log_counters_server_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "server_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Preserve counters for tasks that already emitted logs, so any task
            // still appending after this migration continues past its existing
            // sequences (the unique (task_id, sequence) index would otherwise be
            // violated by a counter restarting at 0). Tasks that never allocated
            // (next = 0) are left to lazy upsert creation.
            migrationBuilder.Sql(
                "INSERT INTO task_log_counters (task_id, next_sequence) " +
                "SELECT id, next_log_sequence FROM server_tasks WHERE next_log_sequence > 0;");

            migrationBuilder.DropColumn(
                name: "next_log_sequence",
                table: "server_tasks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "next_log_sequence",
                table: "server_tasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                "UPDATE server_tasks t SET next_log_sequence = c.next_sequence " +
                "FROM task_log_counters c WHERE t.id = c.task_id;");

            migrationBuilder.DropTable(
                name: "task_log_counters");
        }
    }
}
