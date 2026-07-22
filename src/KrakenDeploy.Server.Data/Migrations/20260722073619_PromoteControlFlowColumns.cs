using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PromoteControlFlowColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "for_each_collection",
                table: "process_steps",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "for_each_parallel",
                table: "process_steps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "max_parallelism",
                table: "process_steps",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "run_on_server",
                table: "process_steps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // D3 — migrate existing rows: lift the four control-flow flags out of
            // the jsonb `config` bag into the new typed columns, then strip the
            // now-redundant keys so the engine has a single source of truth.
            // MaxParallelism is parsed leniently (any integer, incl. non-positive
            // from imported data) — a non-positive value surfaces at runtime as a
            // "batching disabled" warning rather than being silently dropped.
            // ForEach.IterationVariable / .IndexVariable are NOT promoted and stay
            // in config. Note: already-cut release/runbook-run snapshots (jsonb)
            // are intentionally NOT backfilled (pre-production decision) — they
            // deserialize the new fields as type defaults.
            migrationBuilder.Sql(@"
                UPDATE process_steps SET
                    run_on_server = COALESCE((config->>'Octopus.Action.RunOnServer') ILIKE 'true', false),
                    for_each_parallel = COALESCE((config->>'Octopus.Action.ForEach.Parallel') ILIKE 'true', false),
                    for_each_collection = NULLIF(config->>'Octopus.Action.ForEach.Collection', ''),
                    max_parallelism = CASE
                        -- Bound to <=9 digits so a pathological value can't overflow
                        -- int4 and abort the migration; garbage degrades to no cap.
                        WHEN (config->>'Octopus.Action.MaxParallelism') ~ '^-?[0-9]{1,9}$'
                        THEN (config->>'Octopus.Action.MaxParallelism')::int
                        ELSE NULL END;");
            migrationBuilder.Sql(@"
                UPDATE process_steps SET config = config
                    - 'Octopus.Action.RunOnServer'
                    - 'Octopus.Action.ForEach.Collection'
                    - 'Octopus.Action.ForEach.Parallel'
                    - 'Octopus.Action.MaxParallelism';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-inject the typed columns back into the jsonb `config` bag as the
            // Octopus-compatible keys before dropping the columns, so the rollback
            // is loss-free (emit-only-when-set, mirroring the export boundary).
            migrationBuilder.Sql(@"
                UPDATE process_steps SET config = config
                    || CASE WHEN run_on_server THEN jsonb_build_object('Octopus.Action.RunOnServer', 'true') ELSE '{}'::jsonb END
                    || CASE WHEN for_each_parallel THEN jsonb_build_object('Octopus.Action.ForEach.Parallel', 'true') ELSE '{}'::jsonb END
                    || CASE WHEN for_each_collection IS NOT NULL THEN jsonb_build_object('Octopus.Action.ForEach.Collection', for_each_collection) ELSE '{}'::jsonb END
                    || CASE WHEN max_parallelism IS NOT NULL THEN jsonb_build_object('Octopus.Action.MaxParallelism', max_parallelism::text) ELSE '{}'::jsonb END;");

            migrationBuilder.DropColumn(
                name: "for_each_collection",
                table: "process_steps");

            migrationBuilder.DropColumn(
                name: "for_each_parallel",
                table: "process_steps");

            migrationBuilder.DropColumn(
                name: "max_parallelism",
                table: "process_steps");

            migrationBuilder.DropColumn(
                name: "run_on_server",
                table: "process_steps");
        }
    }
}
