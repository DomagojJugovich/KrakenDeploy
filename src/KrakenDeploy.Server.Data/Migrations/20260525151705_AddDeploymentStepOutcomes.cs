using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeploymentStepOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deployment_step_outcomes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_index = table.Column<int>(type: "integer", nullable: false),
                    step_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    started_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_server_side = table.Column<bool>(type: "boolean", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_step_outcomes", x => x.id);
                    table.ForeignKey(
                        name: "fk_deployment_step_outcomes_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_step_outcomes_deployment_id_step_index",
                table: "deployment_step_outcomes",
                columns: new[] { "deployment_id", "step_index" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deployment_step_outcomes");
        }
    }
}
