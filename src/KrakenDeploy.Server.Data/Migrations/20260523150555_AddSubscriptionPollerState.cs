using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionPollerState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscription_deliveries_event_id",
                table: "subscription_deliveries");

            migrationBuilder.CreateTable(
                name: "subscription_poller_state",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_occurred_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_poller_state", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_deliveries_subscription_id_event_id",
                table: "subscription_deliveries",
                columns: new[] { "subscription_id", "event_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subscription_poller_state");

            migrationBuilder.DropIndex(
                name: "ix_subscription_deliveries_subscription_id_event_id",
                table: "subscription_deliveries");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_deliveries_event_id",
                table: "subscription_deliveries",
                column: "event_id");
        }
    }
}
