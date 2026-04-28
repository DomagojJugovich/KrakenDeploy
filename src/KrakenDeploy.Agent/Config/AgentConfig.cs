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
