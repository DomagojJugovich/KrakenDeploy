namespace KrakenDeploy.Contracts.Steps;

/// <summary>
/// Step config keys for the <c>Kraken.IIS</c> step type — a comprehensive superset of
/// <c>Octopus.IIS</c>. Keys are flat strings so they fit the existing string→string
/// step config dictionary; complex sub-configs (bindings, recycle log flags) are
/// encoded as multi-line strings or comma-separated values.
/// <para>
/// All values support Kraken variable expressions; substitution is applied
/// server-side before the config reaches the agent.
/// </para>
/// </summary>
public static class KrakenIisConfigKeys
{
    private const string Prefix = "Kraken.IIS.";

    // ── General ────────────────────────────────────────────────────────────────

    /// <summary>Required. IIS site name (created if missing).</summary>
    public const string SiteName = Prefix + "SiteName";

    /// <summary>
    /// Required. Base filesystem directory for the site root. In atomic-swap mode
    /// versioned subdirectories are created underneath (e.g. <c>{WebRoot}\v-2025.04.27.123\</c>)
    /// and the IIS physicalPath is updated to point at the active version.
    /// </summary>
    public const string WebRoot = Prefix + "WebRoot";

    /// <summary>Optional. Virtual path within the site for sub-applications. Default: <c>/</c>.</summary>
    public const string AppPath = Prefix + "AppPath";

    // ── App Pool ───────────────────────────────────────────────────────────────

    public const string AppPoolName            = Prefix + "AppPool.Name";
    public const string AppPoolRuntimeVersion  = Prefix + "AppPool.RuntimeVersion";   // "v4.0" | "" (No Managed Code)
    public const string AppPoolPipelineMode    = Prefix + "AppPool.PipelineMode";     // Integrated | Classic
    public const string AppPoolEnable32Bit     = Prefix + "AppPool.Enable32Bit";      // true | false
    public const string AppPoolLoadUserProfile = Prefix + "AppPool.LoadUserProfile";  // true | false
    public const string AppPoolIdentityType    = Prefix + "AppPool.IdentityType";     // ApplicationPoolIdentity | LocalSystem | LocalService | NetworkService | SpecificUser
    public const string AppPoolUsername        = Prefix + "AppPool.Username";
    public const string AppPoolPassword        = Prefix + "AppPool.Password";         // sensitive
    public const string AppPoolIdleTimeoutMin  = Prefix + "AppPool.IdleTimeoutMinutes";
    public const string AppPoolStartMode       = Prefix + "AppPool.StartMode";        // OnDemand | AlwaysRunning
    public const string AppPoolQueueLength     = Prefix + "AppPool.QueueLength";

    // ── Recycling ──────────────────────────────────────────────────────────────

    public const string RecycleRegularInterval   = Prefix + "Recycle.RegularTimeIntervalMinutes";
    public const string RecyclePrivateMemoryKB   = Prefix + "Recycle.PrivateMemoryLimitKB";
    public const string RecycleVirtualMemoryKB   = Prefix + "Recycle.VirtualMemoryLimitKB";
    public const string RecycleRequestLimit      = Prefix + "Recycle.RequestLimit";
    /// <summary>Semicolon-separated <c>HH:mm</c> times.</summary>
    public const string RecycleSpecificTimes     = Prefix + "Recycle.SpecificTimes";
    public const string RecycleLogEventTime      = Prefix + "Recycle.LogEventTime";
    public const string RecycleLogEventMemory    = Prefix + "Recycle.LogEventMemory";
    public const string RecycleLogEventRequests  = Prefix + "Recycle.LogEventRequests";
    public const string RecycleLogEventSchedule  = Prefix + "Recycle.LogEventSchedule";
    public const string RecycleLogEventConfig    = Prefix + "Recycle.LogEventConfig";
    public const string RecycleLogEventIsapi     = Prefix + "Recycle.LogEventIsapi";
    public const string RecycleLogEventOnDemand  = Prefix + "Recycle.LogEventOnDemand";

    // ── Rapid-Fail Protection ──────────────────────────────────────────────────

    public const string RapidFailEnabled         = Prefix + "RapidFail.Enabled";
    public const string RapidFailMaxCrashes      = Prefix + "RapidFail.MaxCrashesPerInterval";
    public const string RapidFailIntervalMinutes = Prefix + "RapidFail.IntervalMinutes";

    // ── Bindings ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Newline-separated bindings. Each line:
    /// <c>protocol|ipAddress|port|hostname|certThumbprint|certStore|sniRequired|sslFlags</c>.
    /// Empty fields are allowed; thumbprint and store are required for HTTPS.
    /// Examples:
    /// <list type="bullet">
    ///   <item><c>http|*|80||||false|0</c></item>
    ///   <item><c>https|*|443|app.example.com|ABCDEF...|My|true|1</c></item>
    /// </list>
    /// </summary>
    public const string Bindings = Prefix + "Bindings";

    // ── Application Init / Preload ─────────────────────────────────────────────

    public const string PreloadEnabled = Prefix + "PreloadEnabled";
    public const string AlwaysRunning  = Prefix + "AlwaysRunning";

    // ── Deploy Strategy ────────────────────────────────────────────────────────

    /// <summary>InPlace | AtomicSwap. Default: AtomicSwap.</summary>
    public const string DeployMode      = Prefix + "Deploy.Mode";
    public const string DeployKeepVersions = Prefix + "Deploy.KeepVersions";
    public const string DeployDrainMode    = Prefix + "Deploy.DrainModeRecycle";

    // ── Health Probe ───────────────────────────────────────────────────────────

    /// <summary>Absolute URL or path relative to the first HTTP binding.</summary>
    public const string HealthCheckUrl                = Prefix + "HealthCheck.Url";
    public const string HealthCheckExpectedStatus     = Prefix + "HealthCheck.ExpectedStatus";
    public const string HealthCheckTimeoutSeconds     = Prefix + "HealthCheck.TimeoutSeconds";
    public const string HealthCheckRetryAttempts      = Prefix + "HealthCheck.RetryAttempts";
    public const string HealthCheckRetryDelaySeconds  = Prefix + "HealthCheck.RetryDelaySeconds";
    public const string HealthCheckExpectedBodyContains = Prefix + "HealthCheck.ExpectedBodyContains";
}
