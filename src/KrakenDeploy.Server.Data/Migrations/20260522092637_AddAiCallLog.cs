using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiCallLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_call_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    feature = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: false),
                    completion_tokens = table.Column<int>(type: "integer", nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(12,6)", nullable: false),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    error_message = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scrubbed_variable_names = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    prompt_body_json = table.Column<string>(type: "jsonb", nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_call_logs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_call_logs_space_id_created_utc",
                table: "ai_call_logs",
                columns: new[] { "space_id", "created_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_call_logs_space_id_feature_created_utc",
                table: "ai_call_logs",
                columns: new[] { "space_id", "feature", "created_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_call_logs");
        }
    }
}
