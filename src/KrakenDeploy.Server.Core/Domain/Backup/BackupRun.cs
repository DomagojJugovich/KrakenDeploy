using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Backup;

/// <summary>
/// One row per completed backup attempt — success or failure. Powers the
/// M13.G.3 health dashboard (last-successful timestamp, last-N runs).
/// Separate from audit_entries so the dashboard query is one table scan
/// instead of "audit rows where event_type=Backup.* parsed out of Details".
/// </summary>
public class BackupRun : Entity
{
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }

    /// <summary>Wall-clock duration. Null on rows that crashed before
    /// reaching the finally block.</summary>
    public TimeSpan? Duration { get; set; }

    public BackupOutcome Outcome { get; set; }

    /// <summary>Absolute path to the bundle directory on success, null on
    /// failure (we either failed before creating it or cleaned up partial
    /// state — see ErrorMessage).</summary>
    public string? BundlePath { get; set; }

    public long BundleSizeBytes { get; set; }

    /// <summary>"User" (manual click) or "Schedule" (Hangfire). Free-form
    /// short label; the dashboard groups by it.</summary>
    public string TriggeredBy { get; set; } = "";

    /// <summary>Operator-friendly failure message. Mirrors the exception
    /// text on the CLI path — safe to display verbatim.</summary>
    public string? ErrorMessage { get; set; }
}

public enum BackupOutcome
{
    Success = 0,
    Failed  = 1,
}
