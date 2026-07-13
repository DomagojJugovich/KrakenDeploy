using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionChildFksDropAttemptNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attempt_number",
                table: "subscription_deliveries");

            // Defensive: the old delete path intentionally orphaned delivery /
            // outbox rows (no FK existed). Purge those orphans before enforcing
            // the cascade FKs so creation can't fail on a legacy DB.
            migrationBuilder.Sql(
                "DELETE FROM subscription_deliveries WHERE subscription_id NOT IN (SELECT id FROM event_subscriptions);");
            migrationBuilder.Sql(
                "DELETE FROM email_digest_outbox WHERE subscription_id NOT IN (SELECT id FROM event_subscriptions);");

            migrationBuilder.AddForeignKey(
                name: "fk_email_digest_outbox_event_subscriptions_subscription_id",
                table: "email_digest_outbox",
                column: "subscription_id",
                principalTable: "event_subscriptions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_subscription_deliveries_event_subscriptions_subscription_id",
                table: "subscription_deliveries",
                column: "subscription_id",
                principalTable: "event_subscriptions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_email_digest_outbox_event_subscriptions_subscription_id",
                table: "email_digest_outbox");

            migrationBuilder.DropForeignKey(
                name: "fk_subscription_deliveries_event_subscriptions_subscription_id",
                table: "subscription_deliveries");

            migrationBuilder.AddColumn<int>(
                name: "attempt_number",
                table: "subscription_deliveries",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
