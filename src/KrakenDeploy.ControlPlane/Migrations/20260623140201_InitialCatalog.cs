using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    host_secret_ref = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shards", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "business_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subdomain = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    tier = table.Column<int>(type: "integer", nullable: false),
                    shard_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conn_secret_ref = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_business_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_business_accounts_shards_shard_id",
                        column: x => x.shard_id,
                        principalTable: "shards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_business_accounts_shard_id",
                table: "business_accounts",
                column: "shard_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_accounts_status",
                table: "business_accounts",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_business_accounts_subdomain",
                table: "business_accounts",
                column: "subdomain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shards_status",
                table: "shards",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "business_accounts");

            migrationBuilder.DropTable(
                name: "shards");
        }
    }
}
