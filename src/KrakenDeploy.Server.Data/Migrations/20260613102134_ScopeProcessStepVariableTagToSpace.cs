using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeProcessStepVariableTagToSpace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "variables",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "tenant_tags",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "runbook_steps",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "runbook_processes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "deployment_steps",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "space_id",
                table: "deployment_processes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill space_id from each row's Space-scoped parent BEFORE the
            // Restrict FK to spaces is added — existing rows default to
            // Guid.Empty, which isn't a valid Space and would fail the FK.
            // Order matters: processes inherit from project/runbook first, then
            // steps inherit from the now-populated process.
            migrationBuilder.Sql(
                "UPDATE deployment_processes dp SET space_id = p.space_id " +
                "FROM projects p WHERE dp.project_id = p.id;");
            migrationBuilder.Sql(
                "UPDATE deployment_steps s SET space_id = dp.space_id " +
                "FROM deployment_processes dp WHERE s.process_id = dp.id;");
            migrationBuilder.Sql(
                "UPDATE runbook_processes rp SET space_id = r.space_id " +
                "FROM runbooks r WHERE rp.runbook_id = r.id;");
            migrationBuilder.Sql(
                "UPDATE runbook_steps s SET space_id = rp.space_id " +
                "FROM runbook_processes rp WHERE s.process_id = rp.id;");
            migrationBuilder.Sql(
                "UPDATE variables v SET space_id = vs.space_id " +
                "FROM variable_sets vs WHERE v.set_id = vs.id;");
            migrationBuilder.Sql(
                "UPDATE tenant_tags t SET space_id = ts.space_id " +
                "FROM tag_sets ts WHERE t.tag_set_id = ts.id;");

            migrationBuilder.CreateIndex(
                name: "ix_variables_space_id",
                table: "variables",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_tags_space_id",
                table: "tenant_tags",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_steps_space_id",
                table: "runbook_steps",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_runbook_processes_space_id",
                table: "runbook_processes",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_steps_space_id",
                table: "deployment_steps",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_deployment_processes_space_id",
                table: "deployment_processes",
                column: "space_id");

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_processes_spaces_space_id",
                table: "deployment_processes",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_deployment_steps_spaces_space_id",
                table: "deployment_steps",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_runbook_processes_spaces_space_id",
                table: "runbook_processes",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_runbook_steps_spaces_space_id",
                table: "runbook_steps",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_tenant_tags_spaces_space_id",
                table: "tenant_tags",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_variables_spaces_space_id",
                table: "variables",
                column: "space_id",
                principalTable: "spaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_deployment_processes_spaces_space_id",
                table: "deployment_processes");

            migrationBuilder.DropForeignKey(
                name: "fk_deployment_steps_spaces_space_id",
                table: "deployment_steps");

            migrationBuilder.DropForeignKey(
                name: "fk_runbook_processes_spaces_space_id",
                table: "runbook_processes");

            migrationBuilder.DropForeignKey(
                name: "fk_runbook_steps_spaces_space_id",
                table: "runbook_steps");

            migrationBuilder.DropForeignKey(
                name: "fk_tenant_tags_spaces_space_id",
                table: "tenant_tags");

            migrationBuilder.DropForeignKey(
                name: "fk_variables_spaces_space_id",
                table: "variables");

            migrationBuilder.DropIndex(
                name: "ix_variables_space_id",
                table: "variables");

            migrationBuilder.DropIndex(
                name: "ix_tenant_tags_space_id",
                table: "tenant_tags");

            migrationBuilder.DropIndex(
                name: "ix_runbook_steps_space_id",
                table: "runbook_steps");

            migrationBuilder.DropIndex(
                name: "ix_runbook_processes_space_id",
                table: "runbook_processes");

            migrationBuilder.DropIndex(
                name: "ix_deployment_steps_space_id",
                table: "deployment_steps");

            migrationBuilder.DropIndex(
                name: "ix_deployment_processes_space_id",
                table: "deployment_processes");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "variables");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "tenant_tags");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "runbook_steps");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "runbook_processes");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "deployment_steps");

            migrationBuilder.DropColumn(
                name: "space_id",
                table: "deployment_processes");
        }
    }
}
