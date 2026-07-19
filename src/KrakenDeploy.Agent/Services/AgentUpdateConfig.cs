namespace KrakenDeploy.Agent.Services;

/// <summary>
/// Agent auto-update configuration. Bound to the "Agent:Update" configuration section.
/// </summary>
public sealed class AgentUpdateConfig
{
    /// <summary>When false, this agent will never check for or apply updates.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often to check for updates. Default 5 minutes.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maintenance window start (local time). Default 02:00.</summary>
    public TimeOnly MaintenanceWindowStart { get; set; } = new(2, 0);

    /// <summary>Maintenance window end (local time). Default 04:00.</summary>
    public TimeOnly MaintenanceWindowEnd { get; set; } = new(4, 0);

    /// <summary>
    /// C6 — how long the newly-installed version has to register healthy after a
    /// self-upgrade restart before the agent automatically rolls back to the
    /// backed-up previous version. This window is granted afresh on every restart
    /// (a delayed restart / machine reboot must not consume it), so it must
    /// comfortably exceed the agent's normal cold-start + registration time.
    /// Default 3 minutes.
    /// </summary>
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// C6 — how many times the new version may restart WITHOUT ever confirming
    /// health before the agent gives up and rolls back. Bounds a crash-loop where
    /// the new binary dies before its per-restart <see cref="HealthCheckTimeout"/>
    /// window elapses (each restart alone would otherwise get a fresh window and
    /// never trip the timeout). Default 3.
    /// </summary>
    public int MaxHealthAttempts { get; set; } = 3;
}
