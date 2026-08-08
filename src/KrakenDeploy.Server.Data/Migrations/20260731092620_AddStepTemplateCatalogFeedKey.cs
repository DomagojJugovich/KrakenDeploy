using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStepTemplateCatalogFeedKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "feed_key",
                table: "step_template_catalog",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_step_template_catalog_feed_key",
                table: "step_template_catalog",
                column: "feed_key");

            // Every pre-SC6 row came from the single hardcoded feed.
            migrationBuilder.Sql(
                "UPDATE step_template_catalog SET feed_key = 'octopusdeploy/library' " +
                "WHERE feed_key = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_step_template_catalog_feed_key",
                table: "step_template_catalog");

            migrationBuilder.DropColumn(
                name: "feed_key",
                table: "step_template_catalog");
        }
    }
}
