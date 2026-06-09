using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryVariableSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_variable_sets_project_id",
                table: "variable_sets");

            migrationBuilder.AlterColumn<Guid>(
                name: "project_id",
                table: "variable_sets",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "variable_sets",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "kind",
                table: "variable_sets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "variable_sets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_variable_set_links",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variable_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_variable_set_links", x => new { x.project_id, x.variable_set_id });
                    table.ForeignKey(
                        name: "fk_project_variable_set_links_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_project_variable_set_links_variable_sets_variable_set_id",
                        column: x => x.variable_set_id,
                        principalTable: "variable_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_variable_sets_project_id",
                table: "variable_sets",
                column: "project_id",
                unique: true,
                filter: "project_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_variable_sets_space_id_kind",
                table: "variable_sets",
                columns: new[] { "space_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_project_variable_set_links_variable_set_id",
                table: "project_variable_set_links",
                column: "variable_set_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_variable_set_links");

            migrationBuilder.DropIndex(
                name: "ix_variable_sets_project_id",
                table: "variable_sets");

            migrationBuilder.DropIndex(
                name: "ix_variable_sets_space_id_kind",
                table: "variable_sets");

            migrationBuilder.DropColumn(
                name: "description",
                table: "variable_sets");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "variable_sets");

            migrationBuilder.DropColumn(
                name: "name",
                table: "variable_sets");

            migrationBuilder.AlterColumn<Guid>(
                name: "project_id",
                table: "variable_sets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_variable_sets_project_id",
                table: "variable_sets",
                column: "project_id",
                unique: true);
        }
    }
}
