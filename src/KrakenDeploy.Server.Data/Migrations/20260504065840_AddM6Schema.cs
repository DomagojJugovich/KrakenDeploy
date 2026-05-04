using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddM6Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "deployments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    variable_set_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_tenants",
                columns: table => new
                {
                    projects_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenants_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_tenants", x => new { x.projects_id, x.tenants_id });
                    table.ForeignKey(
                        name: "fk_project_tenants_projects_projects_id",
                        column: x => x.projects_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_project_tenants_tenants_tenants_id",
                        column: x => x.tenants_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tag_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tag_sets", x => x.id);
                    table.ForeignKey(
                        name: "fk_tag_sets_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_tags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_tags", x => x.id);
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
                name: "ix_deployments_tenant_id",
                table: "deployments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_tenants_tenants_id",
                table: "project_tenants",
                column: "tenants_id");

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
                name: "ix_tenant_tags_tag_set_id_name",
                table: "tenant_tags",
                columns: new[] { "tag_set_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenants_slug",
                table: "tenants",
                column: "slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_deployments_tenants_tenant_id",
                table: "deployments",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_deployments_tenants_tenant_id",
                table: "deployments");

            migrationBuilder.DropTable(
                name: "project_tenants");

            migrationBuilder.DropTable(
                name: "target_tenant_tags");

            migrationBuilder.DropTable(
                name: "tenant_tags");

            migrationBuilder.DropTable(
                name: "tag_sets");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropIndex(
                name: "ix_deployments_tenant_id",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "deployments");
        }
    }
}
