using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RoleAssignmentScopesChildTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "role_assignment_scopes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_assignment_scopes", x => x.id);
                    table.CheckConstraint("ck_role_assignment_scopes_exactly_one_dimension", "num_nonnulls(project_group_id, project_id, environment_id, tenant_id) = 1");
                    table.ForeignKey(
                        name: "fk_role_assignment_scopes_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_assignment_scopes_project_groups_project_group_id",
                        column: x => x.project_group_id,
                        principalTable: "project_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_assignment_scopes_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_assignment_scopes_role_assignments_role_assignment_id",
                        column: x => x.role_assignment_id,
                        principalTable: "role_assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_assignment_scopes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_role_assignment_scopes_environment_id",
                table: "role_assignment_scopes",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_assignment_scopes_project_group_id",
                table: "role_assignment_scopes",
                column: "project_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_assignment_scopes_project_id",
                table: "role_assignment_scopes",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_assignment_scopes_role_assignment_id_project_group_id_",
                table: "role_assignment_scopes",
                columns: new[] { "role_assignment_id", "project_group_id", "project_id", "environment_id", "tenant_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_role_assignment_scopes_tenant_id",
                table: "role_assignment_scopes",
                column: "tenant_id");

            // Data-motion: expand each jsonb Guid array into one child row per
            // element. WHERE EXISTS drops dangling ids (a project/env/tenant/
            // group deleted while the id lingered in the array) so the new
            // per-dimension FKs apply cleanly — this is exactly the "deleted
            // refs stop lingering in grants" the child table is for. tag_ids is
            // dropped, not migrated (dormant).
            migrationBuilder.Sql(@"
                INSERT INTO role_assignment_scopes (id, role_assignment_id, project_group_id)
                SELECT gen_random_uuid(), ra.id, e.val::uuid
                FROM role_assignments ra
                CROSS JOIN LATERAL jsonb_array_elements_text(ra.project_group_ids) AS e(val)
                WHERE EXISTS (SELECT 1 FROM project_groups g WHERE g.id = e.val::uuid);");

            migrationBuilder.Sql(@"
                INSERT INTO role_assignment_scopes (id, role_assignment_id, project_id)
                SELECT gen_random_uuid(), ra.id, e.val::uuid
                FROM role_assignments ra
                CROSS JOIN LATERAL jsonb_array_elements_text(ra.project_ids) AS e(val)
                WHERE EXISTS (SELECT 1 FROM projects p WHERE p.id = e.val::uuid);");

            migrationBuilder.Sql(@"
                INSERT INTO role_assignment_scopes (id, role_assignment_id, environment_id)
                SELECT gen_random_uuid(), ra.id, e.val::uuid
                FROM role_assignments ra
                CROSS JOIN LATERAL jsonb_array_elements_text(ra.environment_ids) AS e(val)
                WHERE EXISTS (SELECT 1 FROM environments env WHERE env.id = e.val::uuid);");

            migrationBuilder.Sql(@"
                INSERT INTO role_assignment_scopes (id, role_assignment_id, tenant_id)
                SELECT gen_random_uuid(), ra.id, e.val::uuid
                FROM role_assignments ra
                CROSS JOIN LATERAL jsonb_array_elements_text(ra.tenant_ids) AS e(val)
                WHERE EXISTS (SELECT 1 FROM tenants t WHERE t.id = e.val::uuid);");

            // Guard against migration-time privilege escalation: an assignment
            // that WAS scoped (any non-empty dimension array) but whose every id
            // was already dangling migrates to zero scope rows, which the matcher
            // reads as whole-Space. Delete those so a dead narrow grant doesn't
            // silently widen (mirrors RoleAssignmentScopeCleanupInterceptor).
            migrationBuilder.Sql(@"
                DELETE FROM role_assignments ra
                WHERE NOT EXISTS (
                        SELECT 1 FROM role_assignment_scopes s WHERE s.role_assignment_id = ra.id)
                  AND (jsonb_array_length(ra.project_group_ids) > 0
                    OR jsonb_array_length(ra.project_ids) > 0
                    OR jsonb_array_length(ra.environment_ids) > 0
                    OR jsonb_array_length(ra.tenant_ids) > 0);");

            migrationBuilder.DropColumn(name: "environment_ids", table: "role_assignments");
            migrationBuilder.DropColumn(name: "project_group_ids", table: "role_assignments");
            migrationBuilder.DropColumn(name: "project_ids", table: "role_assignments");
            migrationBuilder.DropColumn(name: "tag_ids", table: "role_assignments");
            migrationBuilder.DropColumn(name: "tenant_ids", table: "role_assignments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "role_assignment_scopes");

            migrationBuilder.AddColumn<string>(
                name: "environment_ids",
                table: "role_assignments",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "project_group_ids",
                table: "role_assignments",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "project_ids",
                table: "role_assignments",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "tag_ids",
                table: "role_assignments",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "tenant_ids",
                table: "role_assignments",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");
        }
    }
}
