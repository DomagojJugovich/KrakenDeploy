using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManualInterventionInterruptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_server_tasks_running_deployment_peer",
                table: "server_tasks");

            migrationBuilder.AddColumn<string>(
                name: "pause_checkpoint_encrypted",
                table: "server_tasks",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "interruptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_index = table.Column<int>(type: "integer", nullable: false),
                    step_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    instructions = table.Column<string>(type: "text", nullable: true),
                    responsible_team_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    expires_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acted_by_display = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acted_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_interruptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_interruptions_server_tasks_space_id_task_id",
                        columns: x => new { x.space_id, x.task_id },
                        principalTable: "server_tasks",
                        principalColumns: new[] { "space_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_interruptions_users_acted_by_user_id",
                        column: x => x.acted_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_running_deployment_peer",
                table: "server_tasks",
                columns: new[] { "project_id", "environment_id", "tenant_id" },
                filter: "status IN (1, 5, 7) AND kind = 0");

            migrationBuilder.CreateIndex(
                name: "ix_interruptions_acted_by_user_id",
                table: "interruptions",
                column: "acted_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_interruptions_pending_expiry",
                table: "interruptions",
                column: "expires_utc",
                filter: "status = 0 AND expires_utc IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_interruptions_space_id_task_id",
                table: "interruptions",
                columns: new[] { "space_id", "task_id" });

            migrationBuilder.CreateIndex(
                name: "ix_interruptions_task_id_step_index",
                table: "interruptions",
                columns: new[] { "task_id", "step_index" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "interruptions");

            migrationBuilder.DropIndex(
                name: "ix_server_tasks_running_deployment_peer",
                table: "server_tasks");

            migrationBuilder.DropColumn(
                name: "pause_checkpoint_encrypted",
                table: "server_tasks");

            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_running_deployment_peer",
                table: "server_tasks",
                columns: new[] { "project_id", "environment_id", "tenant_id" },
                filter: "status IN (1, 5) AND kind = 0");
        }
    }
}
