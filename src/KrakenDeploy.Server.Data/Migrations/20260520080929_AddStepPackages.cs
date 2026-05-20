using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStepPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "step_packages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    manifest_json = table.Column<string>(type: "jsonb", nullable: false),
                    ui_schema_json = table.Column<string>(type: "jsonb", nullable: true),
                    source = table.Column<int>(type: "integer", nullable: false),
                    step_types = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_step_packages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_step_packages_name",
                table: "step_packages",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_step_packages_name_version",
                table: "step_packages",
                columns: new[] { "name", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "step_packages");
        }
    }
}
