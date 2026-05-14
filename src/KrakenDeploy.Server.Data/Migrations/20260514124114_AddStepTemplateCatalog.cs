using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStepTemplateCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "step_template_catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    community_template_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    path_in_repo = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    file_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    download_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    action_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    author = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    website = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    logo_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    last_synced_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_step_template_catalog", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_step_template_catalog_category",
                table: "step_template_catalog",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_step_template_catalog_community_template_id",
                table: "step_template_catalog",
                column: "community_template_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_step_template_catalog_last_synced_utc",
                table: "step_template_catalog",
                column: "last_synced_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "step_template_catalog");
        }
    }
}
