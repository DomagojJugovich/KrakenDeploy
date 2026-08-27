using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Platform.Migrations
{
    /// <summary>
    /// OnPremBlueGreen-only DDL (BG1/T3): the release registry inside KrakenDb
    /// under the dedicated <c>platform</c> schema, tracked by its OWN history
    /// table (<c>platform.__EFMigrationsHistory_platform</c>) so the app schema's
    /// WP-BASELINE squash never has to account for it. Under Saas this migration
    /// chain is never applied — the catalog's <c>AddReleaseRegistry</c> owns the
    /// same tables there (public schema of the catalog DB).
    /// </summary>
    public partial class InitialPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.CreateTable(
                name: "app_releases",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slot_no = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    deployed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    drained_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    drain_deadline_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_releases", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_settings",
                schema: "platform",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_settings", x => x.key);
                });

            migrationBuilder.CreateIndex(
                name: "ix_app_releases_status",
                schema: "platform",
                table: "app_releases",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_app_releases_slot_no_live",
                schema: "platform",
                table: "app_releases",
                column: "slot_no",
                unique: true,
                filter: "status <> 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_releases",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "platform_settings",
                schema: "platform");
        }
    }
}
