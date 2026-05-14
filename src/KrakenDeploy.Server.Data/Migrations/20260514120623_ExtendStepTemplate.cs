using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExtendStepTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "author",
                table: "step_templates",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "step_templates",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "logo_url",
                table: "step_templates",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "source",
                table: "step_templates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "website",
                table: "step_templates",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_step_templates_category",
                table: "step_templates",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_step_templates_source",
                table: "step_templates",
                column: "source");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_step_templates_category",
                table: "step_templates");

            migrationBuilder.DropIndex(
                name: "ix_step_templates_source",
                table: "step_templates");

            migrationBuilder.DropColumn(
                name: "author",
                table: "step_templates");

            migrationBuilder.DropColumn(
                name: "category",
                table: "step_templates");

            migrationBuilder.DropColumn(
                name: "logo_url",
                table: "step_templates");

            migrationBuilder.DropColumn(
                name: "source",
                table: "step_templates");

            migrationBuilder.DropColumn(
                name: "website",
                table: "step_templates");
        }
    }
}
