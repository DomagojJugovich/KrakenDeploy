using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class CompletePromptedVariables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "prompt_text",
                table: "variables",
                newName: "prompt_description");

            migrationBuilder.AddColumn<bool>(
                name: "is_prompted",
                table: "variables",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "prompt_control",
                table: "variables",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Text");

            migrationBuilder.AddColumn<string>(
                name: "prompt_label",
                table: "variables",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "prompt_options",
                table: "variables",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.Sql(
                "UPDATE variables SET is_prompted = TRUE WHERE prompt_description IS NOT NULL");

            migrationBuilder.Sql(
                """
                UPDATE releases
                SET variable_snapshot = COALESCE((
                    SELECT jsonb_agg(
                        CASE
                            WHEN item->>'promptText' IS NOT NULL
                            THEN (item - 'promptText') || jsonb_build_object(
                                'isPrompted', TRUE,
                                'promptDescription', item->'promptText')
                            ELSE item - 'promptText'
                        END)
                    FROM jsonb_array_elements(variable_snapshot) AS item
                ), '[]'::jsonb)
                WHERE EXISTS (
                    SELECT 1
                    FROM jsonb_array_elements(variable_snapshot) AS item
                    WHERE item->>'promptText' IS NOT NULL
                )
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_prompted",
                table: "variables");

            migrationBuilder.DropColumn(
                name: "prompt_control",
                table: "variables");

            migrationBuilder.DropColumn(
                name: "prompt_label",
                table: "variables");

            migrationBuilder.DropColumn(
                name: "prompt_options",
                table: "variables");

            migrationBuilder.Sql(
                """
                UPDATE releases
                SET variable_snapshot = COALESCE((
                    SELECT jsonb_agg(
                        CASE
                            WHEN COALESCE((item->>'isPrompted')::boolean, FALSE)
                            THEN (item - 'isPrompted' - 'promptLabel' - 'promptDescription'
                                       - 'promptControl' - 'promptOptions')
                                 || jsonb_build_object(
                                     'promptText', COALESCE(item->'promptLabel', item->'promptDescription'))
                            ELSE item - 'isPrompted' - 'promptLabel' - 'promptDescription'
                                      - 'promptControl' - 'promptOptions'
                        END)
                    FROM jsonb_array_elements(variable_snapshot) AS item
                ), '[]'::jsonb)
                """);

            migrationBuilder.RenameColumn(
                name: "prompt_description",
                table: "variables",
                newName: "prompt_text");
        }
    }
}
