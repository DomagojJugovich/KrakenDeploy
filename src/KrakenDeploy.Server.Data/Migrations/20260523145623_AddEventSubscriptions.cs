using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "event_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    event_type_patterns = table.Column<string>(type: "jsonb", nullable: false),
                    project_ids = table.Column<string>(type: "jsonb", nullable: false),
                    environment_ids = table.Column<string>(type: "jsonb", nullable: false),
                    transport = table.Column<int>(type: "integer", nullable: false),
                    transport_config_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    digest_every_minutes = table.Column<int>(type: "integer", nullable: false),
                    disabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_subscriptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscription_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transport = table.Column<int>(type: "integer", nullable: false),
                    started_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    detail = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    error_message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_deliveries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_event_subscriptions_space_id_disabled",
                table: "event_subscriptions",
                columns: new[] { "space_id", "disabled" });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_deliveries_event_id",
                table: "subscription_deliveries",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_deliveries_subscription_id_started_utc",
                table: "subscription_deliveries",
                columns: new[] { "subscription_id", "started_utc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_subscriptions");

            migrationBuilder.DropTable(
                name: "subscription_deliveries");
        }
    }
}
