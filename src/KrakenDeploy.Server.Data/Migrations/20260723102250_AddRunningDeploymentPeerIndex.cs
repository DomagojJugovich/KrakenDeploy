using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRunningDeploymentPeerIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_running_deployment_peer",
                table: "server_tasks",
                columns: new[] { "project_id", "environment_id", "tenant_id" },
                filter: "status IN (1, 5) AND kind = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_server_tasks_running_deployment_peer",
                table: "server_tasks");
        }
    }
}
