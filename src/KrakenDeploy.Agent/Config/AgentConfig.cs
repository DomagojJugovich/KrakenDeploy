using KrakenDeploy.Agent.Deployment;

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
    /// A backstop against pathological fan-out, not a throughput knob. Read once at
    /// startup and CLAMPED to <c>[1, 64]</c> by the gate: a zero would make every shared
    /// acquisition unsatisfiable, and an absurdly large value would silently reinstate
    /// the unbounded fan-out the cap exists to prevent.
    /// <para>
    /// Work beyond the cap QUEUES rather than being refused — but note that is only true
    /// of the GATE. An ad-hoc script's <c>Adhoc:MaxTotalDuration</c> budget spans its
    /// queue wait, so a script that queues behind the cap for its whole budget IS
    /// refused, and its message names the machine rather than the cap. Lower this only
    /// deliberately.
    /// </para>
    /// Default <c>8</c>, kept in one place — <see cref="MachineExecutionGate.DefaultMaxSharedHolders"/>
    /// — so the gate's own fallback and this initializer cannot drift apart.
    /// </summary>
    public int MaxConcurrentSharedWork { get; set; } =
        MachineExecutionGate.DefaultMaxSharedHolders;

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
