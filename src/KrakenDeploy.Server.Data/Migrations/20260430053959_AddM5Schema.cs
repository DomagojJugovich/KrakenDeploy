using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddM5Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "step_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    action_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    properties = table.Column<string>(type: "jsonb", nullable: false),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    community_template_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    imported_from = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_step_templates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_step_templates_action_type",
                table: "step_templates",
                column: "action_type");

            migrationBuilder.CreateIndex(
                name: "ix_step_templates_community_template_id",
                table: "step_templates",
                column: "community_template_id");

            migrationBuilder.CreateIndex(
                name: "ix_step_templates_name",
                table: "step_templates",
                column: "name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "step_templates");
        }
    }
}
