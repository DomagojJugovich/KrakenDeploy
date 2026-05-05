using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "project_group_id",
                table: "projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_groups_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_projects_project_group_id",
                table: "projects",
                column: "project_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_groups_space_id",
                table: "project_groups",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_groups_space_id_is_default",
                table: "project_groups",
                columns: new[] { "space_id", "is_default" },
                unique: true,
                filter: "\"is_default\" = true");

            migrationBuilder.CreateIndex(
                name: "ix_project_groups_space_id_slug",
                table: "project_groups",
                columns: new[] { "space_id", "slug" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_projects_project_groups_project_group_id",
                table: "projects",
                column: "project_group_id",
                principalTable: "project_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_projects_project_groups_project_group_id",
                table: "projects");

            migrationBuilder.DropTable(
                name: "project_groups");

            migrationBuilder.DropIndex(
                name: "ix_projects_project_group_id",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "project_group_id",
                table: "projects");
        }
    }
}
