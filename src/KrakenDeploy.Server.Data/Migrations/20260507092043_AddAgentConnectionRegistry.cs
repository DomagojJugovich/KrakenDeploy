using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentConnectionRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // UNLOGGED table — no WAL overhead, fast inserts/deletes, acceptable
            // for ephemeral agent connection state. Truncated on server restart.
            migrationBuilder.Sql("""
                CREATE UNLOGGED TABLE IF NOT EXISTS agent_connections (
                    connection_id    text        PRIMARY KEY,
                    target_id        uuid        NOT NULL,
                    connected_at_utc timestamptz NOT NULL DEFAULT now()
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS agent_connections;");
        }
    }
}
