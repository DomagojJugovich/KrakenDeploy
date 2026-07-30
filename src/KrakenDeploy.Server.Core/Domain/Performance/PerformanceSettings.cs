using KrakenDeploy.Server.Core.Domain.Settings;

namespace KrakenDeploy.Server.Core.Domain.Performance;

/// <summary>
/// Singleton-row table holding instance-wide performance + retention knobs
/// surfaced on the <c>/configuration/performance</c> page (M13.F.3).
///
/// <para>
/// Three knob categories:
/// <list type="bullet">
///   <item><b>Hangfire worker count</b> — direct CPU/memory dial.
///         <see cref="HangfireWorkerCount"/> replaces the hardcoded
///         <c>options.WorkerCount = 4</c> in <c>Program.cs</c>. Read at
///         startup; the server must be restarted for a change to take
///         effect (Hangfire's worker count is a builder-time setting).</item>
///   <item><b>Slow-deployment thresholds</b> — when a deployment / step
///         exceeds the threshold, an audit event is emitted that operators
///         can subscribe to (M13.B.2/3) and route to webhook / email /
///         runbook / AI inspection.</item>
///   <item><b>Retention windows</b> — promoted from
///         <c>appsettings.json</c>-only to UI-editable values. DB wins when
///         set; the existing config keys
///         <c>Retention:AuditLogDays</c> + <c>Retention:AiCallLogDays</c>
///         act as the first-run bootstrap defaults before any operator has
///         touched the Performance page.</item>
/// </list>
/// </para>
///
/// <para>
/// A System-scoped <see cref="ISettingsDocument"/> (key <c>"performance"</c>)
/// in the unified <c>settings</c> table; the accessor caches the snapshot
/// in-memory + invalidates on write.
/// </para>
/// </summary>
public class PerformanceSettings : ISettingsDocument
{
    /// <inheritdoc />
    public static string Key => "performance";

    /// <inheritdoc />
    public static SettingsScope Scope => SettingsScope.System;

    // ── Hangfire ──────────────────────────────────────────────────────────

    /// <summary>
    /// Worker-thread count for the Hangfire server. Default 4 matches the
    /// previous hardcoded value. Operators on larger boxes raise this to
    /// 8-12 when the job queue starts backing up. Set at startup;
    /// requires a restart for changes to take effect.
    /// </summary>
    public int HangfireWorkerCount { get; set; } = DefaultHangfireWorkerCount;

    /// <summary>Bootstrap default applied when no row exists yet.</summary>
    public const int DefaultHangfireWorkerCount = 4;

    // ── Slow-deployment audit thresholds ──────────────────────────────────

    /// <summary>
    /// Threshold for emitting the <c>Deployment.Slow</c> audit event.
    /// When a deployment's total runtime (<c>CompletedUtc - StartedUtc</c>)
    /// exceeds this many minutes, the audit is written so operators
    /// subscribed to the event can route a notification.
    /// <para>
    /// Zero (or negative) disables the audit. Default 30 minutes — fits
    /// the "this deployment is taking unusually long" intuition without
    /// firing on routine multi-stage releases.
    /// </para>
    /// </summary>
    public int SlowDeploymentThresholdMinutes { get; set; } = DefaultSlowDeploymentThresholdMinutes;

    public const int DefaultSlowDeploymentThresholdMinutes = 30;

    /// <summary>
    /// Threshold for emitting the <c>DeploymentStep.Slow</c> audit event.
    /// When a single step exceeds this many minutes, the audit fires for
    /// that step. Default 10 minutes.
    /// <para>
    /// <strong>v1 limitation:</strong> only server-side steps are timed
    /// inside the orchestrator today; target-side per-step timing needs
    /// the agent to report step boundaries, which is a follow-up
    /// contract change. The threshold is still honoured for server steps.
    /// </para>
    /// </summary>
    public int SlowStepThresholdMinutes { get; set; } = DefaultSlowStepThresholdMinutes;

    public const int DefaultSlowStepThresholdMinutes = 10;

    // ── Retention windows (promoted from appsettings to UI) ──────────────

    /// <summary>
    /// How many days of <c>audit_entries</c> to keep. <c>AuditRetentionJob</c>
    /// reads this value (falling back to <c>Retention:AuditLogDays</c> in
    /// appsettings.json, then to 365). Zero disables the purge.
    /// <para>
    /// Honours the M13.F.5 <c>audit.purge-enabled</c> kill-switch as well:
    /// even with a non-zero day-count, the job short-circuits when the
    /// feature flag is off — operators can pause GDPR retention without
    /// losing the configured value.
    /// </para>
    /// </summary>
    public int AuditLogRetentionDays { get; set; } = DefaultAuditLogRetentionDays;

    public const int DefaultAuditLogRetentionDays = 365;

    /// <summary>
    /// How many days of CHANGE-CONTROL audit entries to keep — the
    /// manual-intervention approve / reject / timeout events listed in
    /// <c>InterruptionAuditEvents.ChangeControlEventTypes</c>. Zero (the default) keeps
    /// them INDEFINITELY.
    /// <para>
    /// A separate, longer window because these entries are the durable record of who
    /// approved a production change, and the ordinary
    /// <see cref="AuditLogRetentionDays"/> window is far too short for it (WP3-b). The
    /// <c>interruptions</c> row itself is CASCADE-deleted with its task and
    /// <c>RetentionService</c> hard-deletes tasks after
    /// <c>RetentionKeepDeployments</c> newer runs, so once the audit entry is purged the
    /// answer to "who approved release 2.3.0 to Prod, when, and what did they write" is
    /// gone from the system entirely. RH state-sector change-control obligations
    /// routinely exceed 365 days, so the shipped default is "never purge" and an
    /// operator must opt in to deleting them.
    /// </para>
    /// <para>
    /// Ordinary audit entries are unaffected: the purge applies this window ONLY to the
    /// change-control event types and the <see cref="AuditLogRetentionDays"/> window to
    /// everything else.
    /// </para>
    /// </summary>
    public int ChangeControlAuditRetentionDays { get; set; }
        = DefaultChangeControlAuditRetentionDays;

    /// <summary>Zero — change-control entries are kept indefinitely unless an operator
    /// deliberately sets a window.</summary>
    public const int DefaultChangeControlAuditRetentionDays = 0;

    /// <summary>
    /// How many days of <c>ai_call_logs</c> to keep. Same precedence as
    /// <see cref="AuditLogRetentionDays"/>: DB wins, then
    /// <c>Retention:AiCallLogDays</c> appsettings, then 90.
    /// </summary>
    public int AiCallLogRetentionDays { get; set; } = DefaultAiCallLogRetentionDays;

    public const int DefaultAiCallLogRetentionDays = 90;

    // ── Offline drop ──────────────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c> (default), offline drop bundles embed the self-contained
    /// runner for the target's RID — the target needs no .NET installed, at the
    /// cost of ≈110 MB per bundle. When <c>false</c>, bundles carry data only and
    /// the bootstrap uses a <c>KrakenDeploy.Agent</c> installed on the target's
    /// PATH (suits fleets where the runner is installed once per machine). Read
    /// at offline-drop generation time by <c>DeploymentWorker</c>; an absent
    /// staged runner degrades gracefully either way.
    /// </summary>
    public bool EmbedOfflineRunner { get; set; } = DefaultEmbedOfflineRunner;

    public const bool DefaultEmbedOfflineRunner = true;
}
