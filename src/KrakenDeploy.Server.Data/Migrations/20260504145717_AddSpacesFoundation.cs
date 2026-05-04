using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSpacesFoundation : Migration
    {
        // Mirror of KrakenDeploy.Server.Core.Domain.Common.WellKnown.DefaultSpaceId.
        // Hard-coded here so the migration is self-contained and doesn't depend on
        // domain code (a migration applied later may not match the current code's
        // constants).
        private static readonly Guid DefaultSpaceId =
            new("00000000-0000-0000-0000-00000000d543");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Create spaces table FIRST (FK targets must exist) ───────────
            migrationBuilder.CreateTable(
                name: "spaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_spaces", x => x.id);
                });

            migrationBuilder.CreateIndex("ix_spaces_slug", "spaces", "slug", unique: true);
            migrationBuilder.CreateIndex("ix_spaces_status", "spaces", "status");
            migrationBuilder.CreateIndex(
                name: "ix_spaces_is_default",
                table: "spaces",
                column: "is_default",
                unique: true,
                filter: "\"is_default\" = true");

            // ── 2. Seed the Default Space row so FKs below have a valid target ─
            migrationBuilder.InsertData(
                table: "spaces",
                columns: new[] { "id", "slug", "name", "description", "is_default", "status", "created_utc", "modified_utc" },
                values: new object[]
                {
                    DefaultSpaceId,
                    "default",
                    "Default",
                    "Auto-created Default Space. Used by single-Space on-prem installs and " +
                        "by all pre-Spaces data backfilled during the AddSpacesFoundation migration.",
                    true,
                    /*SpaceStatus.Active*/ 0,
                    DateTimeOffset.UtcNow,
                    null
                });

            // ── 3. Drop old single-column unique indexes that move to composite ─
            migrationBuilder.DropIndex("ix_tenants_slug", "tenants");
            migrationBuilder.DropIndex("ix_projects_slug", "projects");
            migrationBuilder.DropIndex("ix_packages_package_id", "packages");
            migrationBuilder.DropIndex("ix_packages_package_id_version", "packages");
            migrationBuilder.DropIndex("ix_environments_slug", "environments");

            // ── 4. Add space_id NOT NULL DEFAULT <DefaultSpaceId> on each table ─
            // The default backfills all existing rows to the Default Space.
            // Order doesn't matter — these are independent ALTER TABLEs.
            string[] tables =
            [
                "channels",
                "deployment_targets",
                "deployments",
                "environments",
                "lifecycles",
                "packages",
                "projects",
                "releases",
                "runbooks",
                "step_templates",
                "tag_sets",
                "tenants",
                "variable_sets",
            ];

            foreach (var table in tables)
            {
                migrationBuilder.AddColumn<Guid>(
                    name: "space_id",
                    table: table,
                    type: "uuid",
                    nullable: false,
                    defaultValue: DefaultSpaceId);
            }

            // ── 5. Indexes on space_id (per-table, mostly identical) ───────────
            foreach (var table in tables)
            {
                migrationBuilder.CreateIndex(
                    name: $"ix_{table}_space_id",
                    table: table,
                    column: "space_id");
            }

            // Composite unique indexes that include space_id
            migrationBuilder.CreateIndex(
                "ix_environments_space_id_slug", "environments",
                new[] { "space_id", "slug" }, unique: true);
            migrationBuilder.CreateIndex(
                "ix_packages_space_id_package_id", "packages",
                new[] { "space_id", "package_id" });
            migrationBuilder.CreateIndex(
                "ix_packages_space_id_package_id_version", "packages",
                new[] { "space_id", "package_id", "version" }, unique: true);
            migrationBuilder.CreateIndex(
                "ix_projects_space_id_slug", "projects",
                new[] { "space_id", "slug" }, unique: true);
            migrationBuilder.CreateIndex(
                "ix_tenants_space_id_slug", "tenants",
                new[] { "space_id", "slug" }, unique: true);

            // ── 6. FK constraints (RESTRICT — deleting a Space requires explicit
            //      cleanup of its contents, no implicit cascade across the graph) ─
            foreach (var table in tables)
            {
                migrationBuilder.AddForeignKey(
                    name: $"fk_{table}_spaces_space_id",
                    table: table,
                    column: "space_id",
                    principalTable: "spaces",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            string[] tables =
            [
                "channels",
                "deployment_targets",
                "deployments",
                "environments",
                "lifecycles",
                "packages",
                "projects",
                "releases",
                "runbooks",
                "step_templates",
                "tag_sets",
                "tenants",
                "variable_sets",
            ];

            // Drop FKs first so column drops succeed.
            foreach (var table in tables)
            {
                migrationBuilder.DropForeignKey($"fk_{table}_spaces_space_id", table);
            }

            migrationBuilder.DropIndex("ix_tenants_space_id_slug", "tenants");
            migrationBuilder.DropIndex("ix_projects_space_id_slug", "projects");
            migrationBuilder.DropIndex("ix_packages_space_id_package_id_version", "packages");
            migrationBuilder.DropIndex("ix_packages_space_id_package_id", "packages");
            migrationBuilder.DropIndex("ix_environments_space_id_slug", "environments");

            foreach (var table in tables)
            {
                migrationBuilder.DropIndex($"ix_{table}_space_id", table);
                migrationBuilder.DropColumn("space_id", table);
            }

            migrationBuilder.DropTable("spaces");

            // Restore the pre-Spaces single-column unique indexes.
            migrationBuilder.CreateIndex("ix_tenants_slug", "tenants", "slug", unique: true);
            migrationBuilder.CreateIndex("ix_projects_slug", "projects", "slug", unique: true);
            migrationBuilder.CreateIndex(
                "ix_packages_package_id_version", "packages",
                new[] { "package_id", "version" }, unique: true);
            migrationBuilder.CreateIndex("ix_packages_package_id", "packages", "package_id");
            migrationBuilder.CreateIndex("ix_environments_slug", "environments", "slug", unique: true);
        }
    }
}
