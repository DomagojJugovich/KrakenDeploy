using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_display = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    occurred_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    subject_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    subject_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    before_json = table.Column<string>(type: "jsonb", nullable: true),
                    after_json = table.Column<string>(type: "jsonb", nullable: true),
                    details = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_event_type_occurred_utc",
                table: "audit_entries",
                columns: new[] { "event_type", "occurred_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_occurred_utc",
                table: "audit_entries",
                column: "occurred_utc");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_space_id_occurred_utc",
                table: "audit_entries",
                columns: new[] { "space_id", "occurred_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_user_id_occurred_utc",
                table: "audit_entries",
                columns: new[] { "user_id", "occurred_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_entries");
        }
    }
}
