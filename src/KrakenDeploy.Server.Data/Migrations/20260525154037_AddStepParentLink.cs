using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStepParentLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "parent_step_id",
                table: "deployment_steps",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployment_steps_parent_step_id",
                table: "deployment_steps",
                column: "parent_step_id",
                filter: "parent_step_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_steps_deployment_steps_parent_step_id",
                table: "deployment_steps",
                column: "parent_step_id",
                principalTable: "deployment_steps",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_deployment_steps_deployment_steps_parent_step_id",
                table: "deployment_steps");

            migrationBuilder.DropIndex(
                name: "ix_deployment_steps_parent_step_id",
                table: "deployment_steps");

            migrationBuilder.DropColumn(
                name: "parent_step_id",
                table: "deployment_steps");
        }
    }
}
