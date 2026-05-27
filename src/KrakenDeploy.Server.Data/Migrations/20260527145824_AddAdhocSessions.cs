using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdhocSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adhoc_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prompt = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    mode = table.Column<int>(type: "integer", nullable: false),
                    frozen_target_set_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_display = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    max_iterations = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_adhoc_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "adhoc_iterations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    iter_number = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    generated_script = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    risk_assessment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    expected_output_shape = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    requires_mutation = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    script_signature = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_display = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    approved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    results_json = table.Column<string>(type: "jsonb", nullable: false),
                    verdict = table.Column<int>(type: "integer", nullable: false),
                    narrative = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    llm_model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    llm_prompt_tokens = table.Column<int>(type: "integer", nullable: false),
                    llm_completion_tokens = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_adhoc_iterations", x => x.id);
                    table.ForeignKey(
                        name: "fk_adhoc_iterations_adhoc_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "adhoc_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_adhoc_iterations_session_id_iter_number",
                table: "adhoc_iterations",
                columns: new[] { "session_id", "iter_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_adhoc_sessions_space_id_created_utc",
                table: "adhoc_sessions",
                columns: new[] { "space_id", "created_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adhoc_iterations");

            migrationBuilder.DropTable(
                name: "adhoc_sessions");
        }
    }
}
