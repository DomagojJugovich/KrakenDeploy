using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStepTypeRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "step_package_schemas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    schema_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_step_package_schemas", x => x.id);
                    table.ForeignKey(
                        name: "fk_step_package_schemas_step_packages_step_package_id",
                        column: x => x.step_package_id,
                        principalTable: "step_packages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "step_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    featured = table.Column<bool>(type: "boolean", nullable: false),
                    execution_locus = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    serving_package_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    serving_package_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_step_types", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_step_package_schemas_step_package_id_step_type",
                table: "step_package_schemas",
                columns: new[] { "step_package_id", "step_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_step_types_type_id",
                table: "step_types",
                column: "type_id",
                unique: true);

            // ── System rows (SD-7) ─────────────────────────────────────────
            // The two package-less types, with fixed ids so every database
            // gets identical rows. execution_locus: 1 = ServerRunner,
            // 2 = Structural; source: 1 = System.
            migrationBuilder.Sql(
                """
                INSERT INTO step_types
                    (id, type_id, display_name, category, description, featured,
                     execution_locus, source, created_utc)
                VALUES
                    ('c51e97a0-0000-4000-8000-000000000001', 'kraken.stepgroup',
                     'Step Group', 'control',
                     'Container for child steps. Add a ForEach loop, or run multiple actions in parallel on the same target.',
                     true, 2, 1, now()),
                    ('c51e97a0-0000-4000-8000-000000000002', 'octopus.deployrelease',
                     'Deploy a Release', 'other',
                     'Server-side child deployment of another project''s release.',
                     false, 1, 1, now())
                ON CONFLICT (type_id) DO NOTHING;
                """);

            // ── Backfill from installed packages ───────────────────────────
            // One row per distinct claimed type, id-only metadata (display
            // name = type id), serving package picked by max version string —
            // an approximation of the semver pick that the SC3 registry
            // rebuild replaces with real manifest metadata + a true semver
            // winner on the next startup. execution_locus 0 = AgentPackage,
            // source 0 = Package.
            migrationBuilder.Sql(
                """
                INSERT INTO step_types
                    (id, type_id, display_name, featured, execution_locus, source,
                     serving_package_name, serving_package_version, created_utc)
                SELECT gen_random_uuid(), t.type_id, t.type_id, false, 0, 0,
                       t.name, t.version, now()
                FROM (
                    SELECT DISTINCT ON (x.type_id) x.type_id, p.name, p.version
                    FROM step_packages p,
                         unnest(string_to_array(p.step_types, ',')) AS x(type_id)
                    WHERE btrim(x.type_id) <> ''
                    ORDER BY x.type_id, p.version DESC
                ) t
                ON CONFLICT (type_id) DO NOTHING;
                """);

            // ── Retire Source=BuiltIn template rows (SD-8) ─────────────────
            // The picker derives built-in cards from the registry now; the
            // seeder that recreated these rows is deleted in the same change.
            // source 1 = StepTemplateSource.BuiltIn. One-way — Down does not
            // resurrect them (the enum value survives for historical data).
            migrationBuilder.Sql("DELETE FROM step_templates WHERE source = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "step_package_schemas");

            migrationBuilder.DropTable(
                name: "step_types");
        }
    }
}
