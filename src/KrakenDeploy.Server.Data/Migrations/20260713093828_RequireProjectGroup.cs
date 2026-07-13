using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RequireProjectGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill ungrouped projects into their Space's Default Project
            // Group (identified by is_default). Every Space is seeded with one
            // by SpaceService.EnsureDefaultProjectGroupAsync, so this resolves
            // for all rows; if a Space somehow lacks one, the NOT NULL alter
            // below fails loudly rather than inventing an invalid FK value.
            migrationBuilder.Sql(@"
                UPDATE projects p
                SET project_group_id = g.id
                FROM project_groups g
                WHERE g.space_id = p.space_id
                  AND g.is_default
                  AND p.project_group_id IS NULL;");

            migrationBuilder.AlterColumn<Guid>(
                name: "project_group_id",
                table: "projects",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "project_group_id",
                table: "projects",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }
    }
}
