using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExtendedTagSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Destructive by design (docs/extended-tag-sets-plan.md): the old
            // tenant-owned sets are wiped rather than migrated — same-named sets
            // across tenants would collide on the new (space_id, name) unique
            // index, and legacy rows have no valid scopes/type. `seed-demo`
            // re-creates demo tag data on the new model.
            migrationBuilder.Sql("DELETE FROM tag_sets;");

            migrationBuilder.DropForeignKey(
                name: "fk_tag_sets_tenants_tenant_id",
                table: "tag_sets");

            migrationBuilder.DropTable(
                name: "target_tenant_tags");

            migrationBuilder.DropTable(
                name: "tenant_tags");

            migrationBuilder.DropIndex(
                name: "ix_tag_sets_tenant_id_name",
                table: "tag_sets");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "tag_sets");

            migrationBuilder.RenameColumn(
                name: "tenant_tag_ids",
                table: "role_assignments",
                newName: "tag_ids");

            migrationBuilder.RenameColumn(
                name: "tenant_tag_canonical_names",
                table: "deployment_freezes",
                newName: "tag_ids");

            // The renamed column previously held canonical-name STRINGS; it is
            // read as a Guid list now — reset any legacy content (the dimension
            // was dormant, no UI ever wrote it; belt-and-braces for seeded rows).
            migrationBuilder.Sql("UPDATE deployment_freezes SET tag_ids = '[]'::jsonb;");

            migrationBuilder.AddColumn<string>(
                name: "scopes",
                table: "tag_sets",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "type",
                table: "tag_sets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tags", x => x.id);
                    table.ForeignKey(
                        name: "fk_tags_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tags_tag_sets_tag_set_id",
                        column: x => x.tag_set_id,
                        principalTable: "tag_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tag_applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entity_kind = table.Column<int>(type: "integer", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    free_text_value = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    set_type = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tag_applications", x => x.id);
                    table.ForeignKey(
                        name: "fk_tag_applications_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tag_applications_tag_sets_tag_set_id",
                        column: x => x.tag_set_id,
                        principalTable: "tag_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_tag_applications_tags_tag_id",
                        column: x => x.tag_id,
                        principalTable: "tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tag_sets_space_id_name",
                table: "tag_sets",
                columns: new[] { "space_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tag_applications_entity_kind_entity_id",
                table: "tag_applications",
                columns: new[] { "entity_kind", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tag_applications_single_value_per_set",
                table: "tag_applications",
                columns: new[] { "tag_set_id", "entity_kind", "entity_id" },
                unique: true,
                filter: "set_type IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "ix_tag_applications_space_id",
                table: "tag_applications",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_tag_applications_tag_id",
                table: "tag_applications",
                column: "tag_id");

            migrationBuilder.CreateIndex(
                name: "ix_tag_applications_tag_set_id_entity_kind_entity_id_tag_id",
                table: "tag_applications",
                columns: new[] { "tag_set_id", "entity_kind", "entity_id", "tag_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tags_space_id",
                table: "tags",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_tags_tag_set_id_name",
                table: "tags",
                columns: new[] { "tag_set_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tag_applications");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropIndex(
                name: "ix_tag_sets_space_id_name",
                table: "tag_sets");

            migrationBuilder.DropColumn(
                name: "scopes",
                table: "tag_sets");

            migrationBuilder.DropColumn(
                name: "type",
                table: "tag_sets");

            migrationBuilder.RenameColumn(
                name: "tag_ids",
                table: "role_assignments",
                newName: "tenant_tag_ids");

            migrationBuilder.RenameColumn(
                name: "tag_ids",
                table: "deployment_freezes",
                newName: "tenant_tag_canonical_names");

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "tag_sets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "tenant_tags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_tags", x => x.id);
                    table.ForeignKey(
                        name: "fk_tenant_tags_spaces_space_id",
                        column: x => x.space_id,
                        principalTable: "spaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tenant_tags_tag_sets_tag_set_id",
                        column: x => x.tag_set_id,
                        principalTable: "tag_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "target_tenant_tags",
                columns: table => new
                {
                    targets_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_tags_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_target_tenant_tags", x => new { x.targets_id, x.tenant_tags_id });
                    table.ForeignKey(
                        name: "fk_target_tenant_tags_deployment_targets_targets_id",
                        column: x => x.targets_id,
                        principalTable: "deployment_targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_target_tenant_tags_tenant_tags_tenant_tags_id",
                        column: x => x.tenant_tags_id,
                        principalTable: "tenant_tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tag_sets_tenant_id_name",
                table: "tag_sets",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_target_tenant_tags_tenant_tags_id",
                table: "target_tenant_tags",
                column: "tenant_tags_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_tags_space_id",
                table: "tenant_tags",
                column: "space_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_tags_tag_set_id_name",
                table: "tenant_tags",
                columns: new[] { "tag_set_id", "name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_tag_sets_tenants_tenant_id",
                table: "tag_sets",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
