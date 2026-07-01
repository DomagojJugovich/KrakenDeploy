using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <summary>
    /// Drops the unused <c>agent_connections</c> table. The agent connection registry is
    /// now in-memory in all modes (PostgresAgentConnectionRegistry was removed); the table
    /// was written but never read, so it is dead weight. UNLOGGED + no readers means
    /// dropping it is safe on every tenant DB and the base DB. Recreated on Down.
    /// </summary>
    public partial class DropAgentConnectionsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS agent_connections;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Recreate exactly as AddAgentConnectionRegistry created it.
            migrationBuilder.Sql("""
                CREATE UNLOGGED TABLE IF NOT EXISTS agent_connections (
                    connection_id    text        PRIMARY KEY,
                    target_id        uuid        NOT NULL,
                    connected_at_utc timestamptz NOT NULL DEFAULT now()
                );
                """);
        }
    }
}
