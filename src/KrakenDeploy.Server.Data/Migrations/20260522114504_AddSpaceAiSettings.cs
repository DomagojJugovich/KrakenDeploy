using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSpaceAiSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "space_ai_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    api_key_encrypted = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    base_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    budget_usd_per_month = table.Column<decimal>(type: "numeric(12,6)", nullable: false),
                    log_prompt_bodies = table.Column<bool>(type: "boolean", nullable: false),
                    diagnosis_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    mcp_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    adhoc_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    assistant_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_space_ai_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_space_ai_settings_space_id",
                table: "space_ai_settings",
                column: "space_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "space_ai_settings");
        }
    }
}
