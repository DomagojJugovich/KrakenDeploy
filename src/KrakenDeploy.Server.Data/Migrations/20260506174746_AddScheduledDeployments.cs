using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledDeployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "scheduled_for",
                table: "deployments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_deployments_scheduled_for",
                table: "deployments",
                column: "scheduled_for",
                filter: "scheduled_for IS NOT NULL AND status = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deployments_scheduled_for",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "scheduled_for",
                table: "deployments");
        }
    }
}
