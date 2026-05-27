using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeploymentDiagnosis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deployment_diagnoses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    probable_cause = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    confidence = table.Column<int>(type: "integer", nullable: false),
                    suggested_fix = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    relevant_log_lines_json = table.Column<string>(type: "jsonb", nullable: false),
                    model_used = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: false),
                    completion_tokens = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deployment_diagnoses", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deployment_diagnoses_deployment_id",
                table: "deployment_diagnoses",
                column: "deployment_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deployment_diagnoses");
        }
    }
}
