using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class CapAuditDetailsLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Postgres refuses `ALTER COLUMN … TYPE varchar(n)` outright when any row is
            // longer than n, so the over-long rows have to be trimmed first. Hand-added:
            // EF cannot scaffold this, and without it the migration fails on exactly the
            // databases that most need the cap.
            migrationBuilder.Sql(
                "UPDATE audit_entries " +
                "SET details = left(details, 4095) || '…' " +
                "WHERE length(details) > 4096;");

            migrationBuilder.AlterColumn<string>(
                name: "details",
                table: "audit_entries",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "details",
                table: "audit_entries",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4096)",
                oldMaxLength: 4096,
                oldNullable: true);
        }
    }
}
