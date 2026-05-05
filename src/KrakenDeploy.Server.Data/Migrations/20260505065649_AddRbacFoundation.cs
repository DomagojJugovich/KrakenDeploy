using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <summary>
    /// Adds the M10 RBAC tables (roles, teams, team_members, team_external_groups,
    /// role_assignments, identity_providers) and removes the unused
    /// ASP.NET Identity role tables (the project never enabled .AddRoles()
    /// against IdentityRole — KrakenDeploy uses its own Role/Team/RoleAssignment
    /// model in Server.Core.Domain.Security).
    /// </summary>
    public partial class AddRbacFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Drop unused Identity-managed role tables ───────────────────
            // These were created by the AddIdentity migration but never used
            // — the project always authenticated by user, never by Identity
            // role. Dropping them frees up the "roles" table name for our
            // own domain Role.
            migrationBuilder.DropTable(name: "role_claims");
            migrationBuilder.DropTable(name: "user_roles");
            migrationBuilder.DropTable(name: "roles");

            // ── 2. KrakenDeploy domain Role ───────────────────────────────────
            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id                  = table.Column<Guid>(type: "uuid", nullable: false),
                    name                = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description         = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    granted_permissions = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    is_built_in         = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_system_only      = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_utc         = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc        = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table => table.PrimaryKey("pk_roles", x => x.id));

            migrationBuilder.CreateIndex("ix_roles_name", "roles", "name", unique: true);

            // ── 3. Teams (system-level when space_id is NULL) ─────────────────
            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    id               = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id         = table.Column<Guid>(type: "uuid", nullable: true),
                    name             = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description      = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_built_in      = table.Column<bool>(type: "boolean", nullable: false),
                    is_everyone_team = table.Column<bool>(type: "boolean", nullable: false),
                    created_utc      = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc     = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table => table.PrimaryKey("pk_teams", x => x.id));

            migrationBuilder.CreateIndex("ix_teams_space_id", "teams", "space_id");
            migrationBuilder.CreateIndex(
                "ix_teams_space_id_name", "teams",
                new[] { "space_id", "name" }, unique: true);

            // ── 4. Identity Providers (FK back to teams.default_team_id) ──────
            migrationBuilder.CreateTable(
                name: "identity_providers",
                columns: table => new
                {
                    id                      = table.Column<Guid>(type: "uuid", nullable: false),
                    name                    = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    type                    = table.Column<int>(type: "integer", nullable: false),
                    authority               = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    client_id               = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    client_secret_encrypted = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    scopes                  = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    group_claim_name        = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    auto_provision_users    = table.Column<bool>(type: "boolean", nullable: false),
                    default_team_id         = table.Column<Guid>(type: "uuid", nullable: true),
                    is_enabled              = table.Column<bool>(type: "boolean", nullable: false),
                    icon_url                = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    sort_order              = table.Column<int>(type: "integer", nullable: false),
                    created_utc             = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc            = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_providers", x => x.id);
                    table.ForeignKey(
                        name: "fk_identity_providers_teams_default_team_id",
                        column: x => x.default_team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex("ix_identity_providers_default_team_id",
                "identity_providers", "default_team_id");
            migrationBuilder.CreateIndex("ix_identity_providers_name",
                "identity_providers", "name", unique: true);

            // ── 5. Role Assignments (Team × Role × scope dimensions) ──────────
            migrationBuilder.CreateTable(
                name: "role_assignments",
                columns: table => new
                {
                    id                = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id           = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id           = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id          = table.Column<Guid>(type: "uuid", nullable: true),
                    project_group_ids = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    project_ids       = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    environment_ids   = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    tenant_ids        = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    tenant_tag_ids    = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    created_utc       = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc      = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_role_assignments_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_role_assignments_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex("ix_role_assignments_role_id",
                "role_assignments", "role_id");
            migrationBuilder.CreateIndex("ix_role_assignments_space_id",
                "role_assignments", "space_id");
            migrationBuilder.CreateIndex("ix_role_assignments_team_id_space_id",
                "role_assignments", new[] { "team_id", "space_id" });

            // ── 6. Team Members (composite PK Team×User) ──────────────────────
            migrationBuilder.CreateTable(
                name: "team_members",
                columns: table => new
                {
                    team_id   = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id   = table.Column<Guid>(type: "uuid", nullable: false),
                    added_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_team_members", x => new { x.team_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_team_members_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex("ix_team_members_user_id",
                "team_members", "user_id");

            // ── 7. Team External Groups (IdP group claim → team) ──────────────
            migrationBuilder.CreateTable(
                name: "team_external_groups",
                columns: table => new
                {
                    id                   = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id              = table.Column<Guid>(type: "uuid", nullable: false),
                    identity_provider_id = table.Column<Guid>(type: "uuid", nullable: true),
                    group_claim          = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    display_name         = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_team_external_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_team_external_groups_identity_providers_identity_provider_id",
                        column: x => x.identity_provider_id,
                        principalTable: "identity_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_team_external_groups_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex("ix_team_external_groups_identity_provider_id",
                "team_external_groups", "identity_provider_id");
            migrationBuilder.CreateIndex(
                "ix_team_external_groups_team_id_identity_provider_id_group_cla",
                "team_external_groups",
                new[] { "team_id", "identity_provider_id", "group_claim" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop in reverse order of creation.
            migrationBuilder.DropTable(name: "team_external_groups");
            migrationBuilder.DropTable(name: "team_members");
            migrationBuilder.DropTable(name: "role_assignments");
            migrationBuilder.DropTable(name: "identity_providers");
            migrationBuilder.DropTable(name: "teams");
            migrationBuilder.DropTable(name: "roles");

            // Recreate the Identity-managed role tables that this migration
            // dropped so the AddIdentity migration's Down() still works if
            // someone walks back further.
            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id                = table.Column<Guid>(type: "uuid", nullable: false),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    name              = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_name   = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                },
                constraints: table => table.PrimaryKey("pk_roles", x => x.id));

            migrationBuilder.CreateIndex("RoleNameIndex", "roles", "normalized_name", unique: true);

            migrationBuilder.CreateTable(
                name: "role_claims",
                columns: table => new
                {
                    id          = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy",
                                    Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    claim_type  = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true),
                    role_id     = table.Column<Guid>(type: "uuid", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_role_claims_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex("ix_role_claims_role_id", "role_claims", "role_id");

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex("ix_user_roles_role_id", "user_roles", "role_id");
        }
    }
}
