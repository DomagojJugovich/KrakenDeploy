namespace KrakenDeploy.Agent.Config;

/// <summary>
/// Agent-local configuration. Bound to the "Agent" configuration section.
/// </summary>
public sealed class AgentConfig
{
    /// <summary>
    /// Directory used for persisted identity, logs, and working files.
    /// Defaults to the OS-appropriate location when empty or unset.
    /// </summary>
    public string? DataPath { get; set; }

    /// <summary>
    /// Roles reported to the server via <c>AgentRegistrationRequest</c>.
    /// When empty the server preserves whatever roles were configured in the Targets wizard.
    /// </summary>
    public IReadOnlyList<string> Roles { get; set; } = [];

    /// <summary>
    /// F5 — how many units of work may hold the SHARED side of
    /// <c>MachineExecutionGate</c> at once. In practice that means co-running ad-hoc
    /// scripts, plus deployments to a target with <c>AllowParallelTaskExecution</c>.
    /// A backstop against pathological fan-out, not a throughput knob: work beyond the
    /// cap QUEUES, it is never refused. Read once at startup. Values below 1 are
    /// treated as 1 — a zero would make every shared acquisition unsatisfiable.
    /// Default <c>8</c>.
    /// </summary>
    public int MaxConcurrentSharedWork { get; set; } = 8;

    /// <summary>Resolved data directory, falling back to the OS default when not configured.</summary>
    public string ResolvedDataPath =>
        !string.IsNullOrWhiteSpace(DataPath) ? DataPath : DefaultDataPath();

    private static string DefaultDataPath() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "KrakenDeploy", "Agent")
            : "/var/lib/krakendeploy-agent";
}
