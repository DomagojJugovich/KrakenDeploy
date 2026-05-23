using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailDigestOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_digest_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_digest_outbox", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_digest_outbox_subscription_id_added_utc",
                table: "email_digest_outbox",
                columns: new[] { "subscription_id", "added_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_email_digest_outbox_subscription_id_event_id",
                table: "email_digest_outbox",
                columns: new[] { "subscription_id", "event_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_digest_outbox");
        }
    }
}
