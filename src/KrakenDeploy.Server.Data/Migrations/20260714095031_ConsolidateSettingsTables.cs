using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KrakenDeploy.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidateSettingsTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Create the unified table FIRST so the data-motion below can target it.
            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_type = table.Column<short>(type: "smallint", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_settings_scope_key",
                table: "settings",
                columns: new[] { "scope_type", "scope_id", "key" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            // 2. Fold the six single-purpose tables into typed JSON documents. Property
            //    names are camelCase (JsonSerializerDefaults.Web) and enums are written
            //    as their string names (JsonStringEnumConverter) so the payloads match
            //    exactly what SettingsService serializes. Rows are only inserted where a
            //    source row exists; a missing document is materialised on demand from the
            //    POCO's default property values.

            // 2a. SMTP (System) — tls_mode int → enum name string.
            migrationBuilder.Sql(@"
INSERT INTO settings (id, scope_type, scope_id, key, payload, created_utc, modified_utc)
SELECT gen_random_uuid(), 0, NULL, 'smtp',
    jsonb_build_object(
        'enabled',         enabled,
        'host',            host,
        'port',            port,
        'tlsMode',         CASE tls_mode
                               WHEN 0 THEN 'None'
                               WHEN 1 THEN 'StartTlsWhenAvailable'
                               WHEN 2 THEN 'StartTlsRequired'
                               WHEN 3 THEN 'ImplicitTls'
                               ELSE 'StartTlsWhenAvailable'
                           END,
        'username',        username,
        'passwordEncrypted', password_encrypted,
        'fromAddress',     from_address,
        'fromDisplayName', from_display_name,
        'timeoutSeconds',  timeout_seconds
    ),
    created_utc, modified_utc
FROM smtp_settings;");

            // 2b. Backup (System).
            migrationBuilder.Sql(@"
INSERT INTO settings (id, scope_type, scope_id, key, payload, created_utc, modified_utc)
SELECT gen_random_uuid(), 0, NULL, 'backup',
    jsonb_build_object(
        'targetDirectory', target_directory,
        'scheduleCron',    schedule_cron,
        'scheduleEnabled', schedule_enabled,
        'retainLastN',     retain_last_n
    ),
    created_utc, modified_utc
FROM backup_settings;");

            // 2c. Maintenance (System).
            migrationBuilder.Sql(@"
INSERT INTO settings (id, scope_type, scope_id, key, payload, created_utc, modified_utc)
SELECT gen_random_uuid(), 0, NULL, 'maintenance',
    jsonb_build_object(
        'enabled',         enabled,
        'reason',          reason,
        'enabledByUserId', enabled_by_user_id,
        'enabledUtc',      enabled_utc
    ),
    created_utc, modified_utc
FROM maintenance_settings;");

            // 2d. Performance (System).
            migrationBuilder.Sql(@"
INSERT INTO settings (id, scope_type, scope_id, key, payload, created_utc, modified_utc)
SELECT gen_random_uuid(), 0, NULL, 'performance',
    jsonb_build_object(
        'hangfireWorkerCount',            hangfire_worker_count,
        'slowDeploymentThresholdMinutes', slow_deployment_threshold_minutes,
        'slowStepThresholdMinutes',       slow_step_threshold_minutes,
        'auditLogRetentionDays',          audit_log_retention_days,
        'aiCallLogRetentionDays',         ai_call_log_retention_days,
        'embedOfflineRunner',             embed_offline_runner
    ),
    created_utc, modified_utc
FROM performance_settings;");

            // 2e. Feature flags (System) — the whole table folds into ONE document
            //     holding a map of overrides. Only emitted when at least one row exists.
            migrationBuilder.Sql(@"
INSERT INTO settings (id, scope_type, scope_id, key, payload, created_utc, modified_utc)
SELECT gen_random_uuid(), 0, NULL, 'features',
    jsonb_build_object('overrides', COALESCE(jsonb_object_agg(key, enabled), '{}'::jsonb)),
    now(), NULL
FROM feature_flags
HAVING COUNT(*) > 0;");

            // 2f. AI settings (Space) — one document per Space, keyed by scope_id.
            migrationBuilder.Sql(@"
INSERT INTO settings (id, scope_type, scope_id, key, payload, created_utc, modified_utc)
SELECT gen_random_uuid(), 1, space_id, 'ai',
    jsonb_build_object(
        'provider',               provider,
        'model',                  model,
        'apiKeyEncrypted',        api_key_encrypted,
        'baseUrl',                base_url,
        'budgetUsdPerMonth',      budget_usd_per_month,
        'logPromptBodies',        log_prompt_bodies,
        'diagnosisEnabled',       diagnosis_enabled,
        'mcpEnabled',             mcp_enabled,
        'adhocEnabled',           adhoc_enabled,
        'adhocMaxIterations',     adhoc_max_iterations,
        'adhocTwoPersonApproval', adhoc_two_person_approval,
        'assistantEnabled',       assistant_enabled
    ),
    created_utc, modified_utc
FROM space_ai_settings;");

            // 3. Drop the folded-in tables now that their data lives in `settings`.
            migrationBuilder.DropTable(name: "backup_settings");
            migrationBuilder.DropTable(name: "feature_flags");
            migrationBuilder.DropTable(name: "maintenance_settings");
            migrationBuilder.DropTable(name: "performance_settings");
            migrationBuilder.DropTable(name: "smtp_settings");
            migrationBuilder.DropTable(name: "space_ai_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Structure-only rollback: recreates the six tables EMPTY and drops
            // `settings` — it does NOT copy the folded data (incl. encrypted SMTP
            // password / AI API keys) back. Treat this migration as effectively
            // forward-only; to roll back with data, restore from a backup taken
            // before the upgrade.
            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.CreateTable(
                name: "backup_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retain_last_n = table.Column<int>(type: "integer", nullable: false),
                    schedule_cron = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    schedule_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    target_directory = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "feature_flags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feature_flags", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    enabled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    enabled_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "performance_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ai_call_log_retention_days = table.Column<int>(type: "integer", nullable: false),
                    audit_log_retention_days = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    embed_offline_runner = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    hangfire_worker_count = table.Column<int>(type: "integer", nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    slow_deployment_threshold_minutes = table.Column<int>(type: "integer", nullable: false),
                    slow_step_threshold_minutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_performance_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "smtp_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    from_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    from_display_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    password_encrypted = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    port = table.Column<int>(type: "integer", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    tls_mode = table.Column<int>(type: "integer", nullable: false),
                    username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_smtp_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "space_ai_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    adhoc_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    adhoc_max_iterations = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    adhoc_two_person_approval = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    api_key_encrypted = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    assistant_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    base_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    budget_usd_per_month = table.Column<decimal>(type: "numeric(12,6)", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    diagnosis_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    log_prompt_bodies = table.Column<bool>(type: "boolean", nullable: false),
                    mcp_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    space_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_space_ai_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_feature_flags_key",
                table: "feature_flags",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_space_ai_settings_space_id",
                table: "space_ai_settings",
                column: "space_id",
                unique: true);
        }
    }
}
