using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <summary>
    /// B5 — maps Postgres's <c>xmin</c> system column on <c>server_tasks</c> as
    /// the EF optimistic-concurrency token. This is a MODEL-ONLY change: xmin
    /// exists on every Postgres row already, so there is no DDL to run. The
    /// scaffolder emitted an <c>AddColumn</c> operation for it (removed here per
    /// the Npgsql guidance — Postgres refuses to add a column named like a
    /// system column); the migration exists solely to keep the model snapshot
    /// in sync.
    /// </summary>
    public partial class AddServerTaskXminConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — xmin is a Postgres system column.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — the column is not ours to drop.
        }
    }
}
