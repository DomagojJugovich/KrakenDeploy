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
}
