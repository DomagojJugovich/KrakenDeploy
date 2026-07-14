using KrakenDeploy.Server.Core.Domain.Settings;

namespace KrakenDeploy.Server.Core.Domain.Backup;

/// <summary>
/// Server-wide backup configuration (M13.G). A System-scoped
/// <see cref="ISettingsDocument"/> (key <c>"backup"</c>) in the unified
/// <c>settings</c> table — one backup policy per KrakenDeploy instance.
///
/// <para>
/// The bundle format and engine are inherited verbatim from the existing
/// CLI (<c>BackupCommands</c>) — this document only carries the operator-facing
/// knobs: where bundles go, whether the scheduler is on, and how many to
/// keep around.
/// </para>
/// </summary>
public class BackupSettings : ISettingsDocument
{
    /// <inheritdoc />
    public static string Key => "backup";

    /// <inheritdoc />
    public static SettingsScope Scope => SettingsScope.System;

    /// <summary>
    /// Directory the backup engine writes bundles into. Each run creates a
    /// timestamped subdirectory <c>kraken-backup-{yyyyMMdd-HHmmss}/</c>
    /// under this path. Resolved against the server's working directory if
    /// relative.
    /// </summary>
    public string TargetDirectory { get; set; } = "backups";

    /// <summary>
    /// Hangfire cron expression for the scheduled run, e.g.
    /// <c>"0 2 * * *"</c> (02:00 UTC daily). Only consulted when
    /// <see cref="ScheduleEnabled"/> is true.
    /// </summary>
    public string? ScheduleCron { get; set; } = "0 2 * * *";

    /// <summary>True = the Hangfire recurring job is registered with the
    /// configured cron. False = the schedule entry is unregistered (and
    /// the operator runs Backup-now manually).</summary>
    public bool ScheduleEnabled { get; set; }

    /// <summary>
    /// Keep only the most recent N bundles in <see cref="TargetDirectory"/>;
    /// older bundles get deleted at the end of each successful run. 0 =
    /// keep all (the operator manages retention externally).
    /// </summary>
    public int RetainLastN { get; set; } = 14;
}
