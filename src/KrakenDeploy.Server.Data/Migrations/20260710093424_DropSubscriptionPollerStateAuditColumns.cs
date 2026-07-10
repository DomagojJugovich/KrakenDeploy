using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropSubscriptionPollerStateAuditColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "created_utc",
                table: "subscription_poller_state");

            migrationBuilder.DropColumn(
                name: "modified_utc",
                table: "subscription_poller_state");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_utc",
                table: "subscription_poller_state",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "modified_utc",
                table: "subscription_poller_state",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
