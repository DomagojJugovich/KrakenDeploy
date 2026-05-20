using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStepPackageCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "step_package_catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    download_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    manifest_json = table.Column<string>(type: "jsonb", nullable: false),
                    changelog = table.Column<string>(type: "text", nullable: true),
                    published_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    release_html_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    last_synced_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_step_package_catalog", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_step_package_catalog_name",
                table: "step_package_catalog",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_step_package_catalog_name_version",
                table: "step_package_catalog",
                columns: new[] { "name", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "step_package_catalog");
        }
    }
}
