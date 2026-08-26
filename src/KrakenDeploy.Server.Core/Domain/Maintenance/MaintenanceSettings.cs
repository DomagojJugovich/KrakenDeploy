using KrakenDeploy.Server.Core.Domain.Settings;

namespace KrakenDeploy.Server.Core.Domain.Maintenance;

/// <summary>
/// System-scoped <see cref="ISettingsDocument"/> (key <c>"maintenance"</c>)
/// holding the instance-wide maintenance flag.
/// When <see cref="Enabled"/> is true:
/// <list type="bullet">
///   <item>the maintenance middleware refuses POST/PUT/PATCH/DELETE on
///   most endpoints with HTTP 503 and a body that surfaces
///   <see cref="Reason"/>;</item>
///   <item>NO new server task starts — <c>ServerTaskLease.TryClaimAsync</c>
///   refuses the <c>Queued→Running</c> claim with
///   <c>MaintenanceBlocked</c>, so already-queued and
///   already-due-scheduled deployments stay queued instead of draining
///   through the window. Child tasks (a <c>DeployRelease</c> step's
///   sub-deployment) are exempt so an in-flight parent can finish;</item>
///   <item><i>some</i> Hangfire recurring jobs short-circuit — the ones
///   that opt in via <c>MaintenancePause</c> (backup, subscription
///   poller, digest flush) — so a backup that fires mid-upgrade doesn't
///   race the migration. The rest, notably the dispatch reconciler, keep
///   running by design: its orphan-recovery arm must survive a
///   restart-heavy window.</item>
/// </list>
///
/// <para>
/// In-flight tasks are NOT aborted — they run to completion, and the
/// agent transport stays exempt from the middleware so they can. The
/// contract is "nothing NEW starts", not "everything stops".
/// </para>
///
/// <para>
/// Bypass is permission-gated (<c>BypassMaintenance</c>). Holders of a
/// SYSTEM-scope <c>AdministerSystem</c> assignment always pass (the check is
/// system-wide, so a Space-pinned admin does not — WP3-c);
/// <c>SystemManager</c> gets the bypass explicitly so the delegated-
/// admin tier can still run the maintenance work itself. Normal users
/// hit the 503 wall.
/// </para>
/// </summary>
public class MaintenanceSettings : ISettingsDocument
{
    /// <inheritdoc />
    public static string Key => "maintenance";

    /// <inheritdoc />
    public static SettingsScope Scope => SettingsScope.System;

    /// <summary>Master switch. When true, the middleware blocks writes.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Operator-supplied message shown to users blocked by the gate.
    /// Surfaced verbatim in the 503 response body and on the
    /// LicenseWarningBanner-style indicator inside the UI. Keep it
    /// short and informative — "Upgrading to v1.2 — back online by
    /// 02:00 UTC" reads better than "maintenance".
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>Who flipped the switch on, for audit-log + page display.</summary>
    public Guid? EnabledByUserId { get; set; }

    /// <summary>When the switch was last enabled — null while disabled.</summary>
    public DateTimeOffset? EnabledUtc { get; set; }
}
