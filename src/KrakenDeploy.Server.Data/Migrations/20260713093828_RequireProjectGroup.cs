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
            // Seed the Default Space's Default Project Group (mirrors the Default
            // Space seed in AddSpacesFoundation) so the backfill below has a
            // target and ProjectService can always resolve a group. Guarded: on
            // an existing DB where SpaceService already created one at runtime,
            // the filtered unique (space_id, is_default) index would otherwise be
            // violated, so insert only when absent.
            migrationBuilder.Sql(@"
                INSERT INTO project_groups
                    (id, space_id, slug, name, description, sort_order, is_default, created_utc)
                SELECT '00000000-0000-0000-0000-00000000d6a0'::uuid,
                       '00000000-0000-0000-0000-00000000d543'::uuid,
                       'default', 'Default Project Group',
                       'Auto-created default group for the Default Space.', 0, true, now()
                WHERE NOT EXISTS (
                    SELECT 1 FROM project_groups
                    WHERE space_id = '00000000-0000-0000-0000-00000000d543'::uuid AND is_default);");

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
