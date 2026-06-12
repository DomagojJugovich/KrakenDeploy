using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTargetTenantAndEnvironmentLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "target_environments",
                columns: table => new
                {
                    deployment_target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environments_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_target_environments", x => new { x.deployment_target_id, x.environments_id });
                    table.ForeignKey(
                        name: "fk_target_environments_deployment_targets_deployment_target_id",
                        column: x => x.deployment_target_id,
                        principalTable: "deployment_targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_target_environments_environments_environments_id",
                        column: x => x.environments_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "target_tenants",
                columns: table => new
                {
                    deployment_target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenants_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_target_tenants", x => new { x.deployment_target_id, x.tenants_id });
                    table.ForeignKey(
                        name: "fk_target_tenants_deployment_targets_deployment_target_id",
                        column: x => x.deployment_target_id,
                        principalTable: "deployment_targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_target_tenants_tenants_tenants_id",
                        column: x => x.tenants_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_target_environments_environments_id",
                table: "target_environments",
                column: "environments_id");

            migrationBuilder.CreateIndex(
                name: "ix_target_tenants_tenants_id",
                table: "target_tenants",
                column: "tenants_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "target_environments");

            migrationBuilder.DropTable(
                name: "target_tenants");
        }
    }
}
