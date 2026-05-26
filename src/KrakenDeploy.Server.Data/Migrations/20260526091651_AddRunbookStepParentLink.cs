using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRunbookStepParentLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "parent_step_id",
                table: "runbook_steps",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_runbook_steps_parent_step_id",
                table: "runbook_steps",
                column: "parent_step_id",
                filter: "parent_step_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_runbook_steps_runbook_steps_parent_step_id",
                table: "runbook_steps",
                column: "parent_step_id",
                principalTable: "runbook_steps",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_runbook_steps_runbook_steps_parent_step_id",
                table: "runbook_steps");

            migrationBuilder.DropIndex(
                name: "ix_runbook_steps_parent_step_id",
                table: "runbook_steps");

            migrationBuilder.DropColumn(
                name: "parent_step_id",
                table: "runbook_steps");
        }
    }
}
