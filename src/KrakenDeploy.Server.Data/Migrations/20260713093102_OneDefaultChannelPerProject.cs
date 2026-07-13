using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class OneDefaultChannelPerProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defensive: the old one-default invariant was maintained by a
            // non-transactional clear-then-set, so a race could leave a project
            // with two defaults. Keep the earliest, unset the rest, before the
            // partial-unique index would otherwise reject the row.
            migrationBuilder.Sql(@"
                UPDATE channels SET is_default = false
                WHERE id IN (
                    SELECT id FROM (
                        SELECT id, row_number() OVER (
                            PARTITION BY project_id ORDER BY created_utc, id) AS rn
                        FROM channels WHERE is_default
                    ) ranked WHERE ranked.rn > 1
                );");

            migrationBuilder.CreateIndex(
                name: "ix_channels_project_id",
                table: "channels",
                column: "project_id",
                unique: true,
                filter: "is_default");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_channels_project_id",
                table: "channels");
        }
    }
}
