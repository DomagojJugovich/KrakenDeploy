using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.ControlPlane.Migrations
{
    /// <summary>
    /// BG1/T3 ownership transfer: <c>app_releases</c> + <c>platform_settings</c>
    /// left the catalog MODEL for <c>PlatformReleaseDbContext</c>
    /// (KrakenDeploy.Platform). The physical tables stay exactly where they are —
    /// under Saas the platform context maps the same catalog tables — so this
    /// migration is deliberately EMPTY: the generated <c>DropTable</c> operations
    /// were removed by hand. Fresh catalogs still get the tables from
    /// <c>AddReleaseRegistry</c>, which remains the DDL of record for Saas.
    /// </summary>
    public partial class TransferReleaseRegistryToPlatform : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — model-only ownership transfer (see class doc).
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — the tables never left.
        }
    }
}
