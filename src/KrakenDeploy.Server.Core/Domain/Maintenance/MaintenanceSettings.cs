using KrakenDeploy.Server.Core.Domain.Settings;

namespace KrakenDeploy.Server.Core.Domain.Maintenance;

/// <summary>
/// System-scoped <see cref="ISettingsDocument"/> (key <c>"maintenance"</c>)
/// holding the instance-wide maintenance flag.
/// When <see cref="Enabled"/> is true, the maintenance middleware
/// refuses POST/PUT/PATCH/DELETE on most endpoints with HTTP 503 and a
/// body that surfaces <see cref="Reason"/>. Hangfire recurring jobs
/// short-circuit at the top of their handler too — so a backup that
/// fires mid-upgrade doesn't race the migration.
///
/// <para>
/// Bypass is permission-gated (<c>BypassMaintenance</c>). Sys-admins
/// always pass (via the god-mode <c>AdministerSystem</c> implication);
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
