using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropDeadColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "icon_url",
                table: "identity_providers");

            migrationBuilder.DropColumn(
                name: "sort_order",
                table: "identity_providers");

            migrationBuilder.DropColumn(
                name: "tag_ids",
                table: "deployment_freezes");

            migrationBuilder.DropColumn(
                name: "completion_tokens",
                table: "deployment_diagnoses");

            migrationBuilder.DropColumn(
                name: "model_used",
                table: "deployment_diagnoses");

            migrationBuilder.DropColumn(
                name: "prompt_tokens",
                table: "deployment_diagnoses");

            migrationBuilder.DropColumn(
                name: "llm_completion_tokens",
                table: "adhoc_iterations");

            migrationBuilder.DropColumn(
                name: "llm_model",
                table: "adhoc_iterations");

            migrationBuilder.DropColumn(
                name: "llm_prompt_tokens",
                table: "adhoc_iterations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "icon_url",
                table: "identity_providers",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sort_order",
                table: "identity_providers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "tag_ids",
                table: "deployment_freezes",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "completion_tokens",
                table: "deployment_diagnoses",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "model_used",
                table: "deployment_diagnoses",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "prompt_tokens",
                table: "deployment_diagnoses",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "llm_completion_tokens",
                table: "adhoc_iterations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "llm_model",
                table: "adhoc_iterations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "llm_prompt_tokens",
                table: "adhoc_iterations",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
