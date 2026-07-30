using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <summary>
    /// SC0: step_packages.step_types is a comma-joined claim list matched by
    /// StepPackageResolver with a ",{type}," sentinel ILIKE — a padded entry
    /// (installed from a manifest authored as "A, B") can therefore never be
    /// resolved. Install-side now trims; this backfills rows stored before
    /// the fix (concretely: octopus.tentaclepackage's " kraken.deploypackage").
    /// </summary>
    public partial class TrimStepPackageStepTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE step_packages
                SET step_types = btrim(regexp_replace(step_types, '\s*,\s*', ',', 'g'))
                WHERE step_types <> btrim(regexp_replace(step_types, '\s*,\s*', ',', 'g'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Whitespace normalisation is not reversible (and never needs to be).
        }
    }
}
