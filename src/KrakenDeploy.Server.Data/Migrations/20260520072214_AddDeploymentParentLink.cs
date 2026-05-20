using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeploymentParentLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "parent_deployment_id",
                table: "deployments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployments_parent_deployment_id",
                table: "deployments",
                column: "parent_deployment_id",
                filter: "parent_deployment_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_deployments_deployments_parent_deployment_id",
                table: "deployments",
                column: "parent_deployment_id",
                principalTable: "deployments",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_deployments_deployments_parent_deployment_id",
                table: "deployments");

            migrationBuilder.DropIndex(
                name: "ix_deployments_parent_deployment_id",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "parent_deployment_id",
                table: "deployments");
        }
    }
}
