using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServerTaskProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // cause + created_by_display are NOT NULL in the model, but leaving a
            // column DEFAULT on them would (a) let a raw insert omit them and (b)
            // undermine the "creation guard chose a cause" story. So add nullable,
            // backfill any existing (pre-release) rows with honest values, then
            // ALTER to NOT NULL — no lingering DB default (mirrors RequireProjectGroup).
            migrationBuilder.AddColumn<int>(
                name: "cause",
                table: "server_tasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cause_detail",
                table: "server_tasks",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "created_by_display",
                table: "server_tasks",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "created_by_user_id",
                table: "server_tasks",
                type: "uuid",
                nullable: true);

            // Backfill existing rows: cause 1 = Manual (a pre-existing execution had
            // no recorded provenance; Manual is the least-misleading label), display
            // marks them as migrated. created_by_user_id stays NULL (unknown).
            migrationBuilder.Sql(
                "UPDATE server_tasks SET cause = 1 WHERE cause IS NULL;");
            migrationBuilder.Sql(
                "UPDATE server_tasks SET created_by_display = 'System (migrated)' " +
                "WHERE created_by_display IS NULL;");

            migrationBuilder.AlterColumn<int>(
                name: "cause",
                table: "server_tasks",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "created_by_display",
                table: "server_tasks",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_server_tasks_created_by_user_id",
                table: "server_tasks",
                column: "created_by_user_id",
                filter: "created_by_user_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_server_tasks_users_created_by_user_id",
                table: "server_tasks",
                column: "created_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_server_tasks_users_created_by_user_id",
                table: "server_tasks");

            migrationBuilder.DropIndex(
                name: "ix_server_tasks_created_by_user_id",
                table: "server_tasks");

            migrationBuilder.DropColumn(
                name: "cause",
                table: "server_tasks");

            migrationBuilder.DropColumn(
                name: "cause_detail",
                table: "server_tasks");

            migrationBuilder.DropColumn(
                name: "created_by_display",
                table: "server_tasks");

            migrationBuilder.DropColumn(
                name: "created_by_user_id",
                table: "server_tasks");
        }
    }
}
