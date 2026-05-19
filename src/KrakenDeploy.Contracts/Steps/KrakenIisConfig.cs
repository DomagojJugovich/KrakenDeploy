using System.Globalization;

namespace KrakenDeploy.Contracts.Steps;

/// <summary>
/// Parsed, strongly-typed view of the <c>Kraken.IIS</c> step config.
/// Use <see cref="Parse"/> to build from a flat string→string config dictionary.
/// </summary>
public sealed record KrakenIisConfig
{
    public required string SiteName { get; init; }
    public required string WebRoot { get; init; }
    public string AppPath { get; init; } = "/";

    public KrakenIisAppPool AppPool { get; init; } = new();
    public KrakenIisRecycle Recycle { get; init; } = new();
    public KrakenIisRapidFail RapidFail { get; init; } = new();
    public KrakenIisAuthentication Authentication { get; init; } = new();
    public IReadOnlyList<KrakenIisBinding> Bindings { get; init; } = [];
    public bool PreloadEnabled { get; init; }
    public bool AlwaysRunning { get; init; }
    public KrakenIisDeploy Deploy { get; init; } = new();
    public KrakenIisHealthCheck? HealthCheck { get; init; }

    /// <summary>
    /// Builds a <see cref="KrakenIisConfig"/> from a step config dictionary.
    /// Throws <see cref="InvalidOperationException"/> when required keys are missing.
    /// </summary>
    public static KrakenIisConfig Parse(IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var siteName = GetRequired(config, KrakenIisConfigKeys.SiteName);
        var webRoot  = GetRequired(config, KrakenIisConfigKeys.WebRoot);

        return new KrakenIisConfig
        {
            SiteName = siteName,
            WebRoot  = webRoot,
            AppPath  = GetOrDefault(config, KrakenIisConfigKeys.AppPath, "/"),

            AppPool = new KrakenIisAppPool
            {
                Name              = GetOrDefault(config, KrakenIisConfigKeys.AppPoolName, siteName),
                RuntimeVersion    = GetOrDefault(config, KrakenIisConfigKeys.AppPoolRuntimeVersion, "v4.0"),
                PipelineMode      = GetOrDefault(config, KrakenIisConfigKeys.AppPoolPipelineMode, "Integrated"),
                Enable32Bit       = GetBool(config, KrakenIisConfigKeys.AppPoolEnable32Bit, false),
                LoadUserProfile   = GetBool(config, KrakenIisConfigKeys.AppPoolLoadUserProfile, false),
                IdentityType      = GetOrDefault(config, KrakenIisConfigKeys.AppPoolIdentityType, "ApplicationPoolIdentity"),
                Username          = GetOrNull(config, KrakenIisConfigKeys.AppPoolUsername),
                Password          = GetOrNull(config, KrakenIisConfigKeys.AppPoolPassword),
                IdleTimeoutMinutes = GetInt(config, KrakenIisConfigKeys.AppPoolIdleTimeoutMin, 20),
                StartMode         = GetOrDefault(config, KrakenIisConfigKeys.AppPoolStartMode, "OnDemand"),
                QueueLength       = GetInt(config, KrakenIisConfigKeys.AppPoolQueueLength, 1000),
            },

            Recycle = new KrakenIisRecycle
            {
                RegularIntervalMinutes = GetInt(config, KrakenIisConfigKeys.RecycleRegularInterval, 1740),
                PrivateMemoryLimitKB   = GetIntOrNull(config, KrakenIisConfigKeys.RecyclePrivateMemoryKB),
                VirtualMemoryLimitKB   = GetIntOrNull(config, KrakenIisConfigKeys.RecycleVirtualMemoryKB),
                RequestLimit           = GetIntOrNull(config, KrakenIisConfigKeys.RecycleRequestLimit),
                SpecificTimes          = ParseTimes(GetOrNull(config, KrakenIisConfigKeys.RecycleSpecificTimes)),
                LogEventTime      = GetBool(config, KrakenIisConfigKeys.RecycleLogEventTime, true),
                LogEventMemory    = GetBool(config, KrakenIisConfigKeys.RecycleLogEventMemory, true),
                LogEventRequests  = GetBool(config, KrakenIisConfigKeys.RecycleLogEventRequests, true),
                LogEventSchedule  = GetBool(config, KrakenIisConfigKeys.RecycleLogEventSchedule, true),
                LogEventConfig    = GetBool(config, KrakenIisConfigKeys.RecycleLogEventConfig, true),
                LogEventIsapi     = GetBool(config, KrakenIisConfigKeys.RecycleLogEventIsapi, true),
                LogEventOnDemand  = GetBool(config, KrakenIisConfigKeys.RecycleLogEventOnDemand, true),
            },

            RapidFail = new KrakenIisRapidFail
            {
                Enabled              = GetBool(config, KrakenIisConfigKeys.RapidFailEnabled, true),
                MaxCrashesPerInterval = GetInt(config, KrakenIisConfigKeys.RapidFailMaxCrashes, 5),
                IntervalMinutes      = GetInt(config, KrakenIisConfigKeys.RapidFailIntervalMinutes, 5),
            },

            Authentication = new KrakenIisAuthentication
            {
                AnonymousEnabled = GetBool(config, KrakenIisConfigKeys.AuthenticationAnonymousEnabled, true),
                BasicEnabled     = GetBool(config, KrakenIisConfigKeys.AuthenticationBasicEnabled, false),
                WindowsEnabled   = GetBool(config, KrakenIisConfigKeys.AuthenticationWindowsEnabled, false),
            },

            Bindings = KrakenIisBinding.ParseAll(GetOrNull(config, KrakenIisConfigKeys.Bindings)),
            PreloadEnabled = GetBool(config, KrakenIisConfigKeys.PreloadEnabled, false),
            AlwaysRunning  = GetBool(config, KrakenIisConfigKeys.AlwaysRunning, false),

            Deploy = new KrakenIisDeploy
            {
                Mode              = GetOrDefault(config, KrakenIisConfigKeys.DeployMode, "AtomicSwap"),
                KeepVersions      = GetInt(config, KrakenIisConfigKeys.DeployKeepVersions, 5),
                DrainModeRecycle  = GetBool(config, KrakenIisConfigKeys.DeployDrainMode, true),
            },

            HealthCheck = string.IsNullOrEmpty(GetOrNull(config, KrakenIisConfigKeys.HealthCheckUrl))
                ? null
                : new KrakenIisHealthCheck
                {
                    Url                  = GetRequired(config, KrakenIisConfigKeys.HealthCheckUrl),
                    ExpectedStatus       = GetInt(config, KrakenIisConfigKeys.HealthCheckExpectedStatus, 200),
                    TimeoutSeconds       = GetInt(config, KrakenIisConfigKeys.HealthCheckTimeoutSeconds, 30),
                    RetryAttempts        = GetInt(config, KrakenIisConfigKeys.HealthCheckRetryAttempts, 5),
                    RetryDelaySeconds    = GetInt(config, KrakenIisConfigKeys.HealthCheckRetryDelaySeconds, 3),
                    ExpectedBodyContains = GetOrNull(config, KrakenIisConfigKeys.HealthCheckExpectedBodyContains),
                },
        };
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string GetRequired(IReadOnlyDictionary<string, string> c, string key)
        => c.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.Trim()
            : throw new InvalidOperationException($"Required Kraken.IIS config key '{key}' is missing or blank.");

    private static string GetOrDefault(IReadOnlyDictionary<string, string> c, string key, string fallback)
        => c.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

    private static string? GetOrNull(IReadOnlyDictionary<string, string> c, string key)
        => c.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static bool GetBool(IReadOnlyDictionary<string, string> c, string key, bool fallback)
        => c.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    private static int GetInt(IReadOnlyDictionary<string, string> c, string key, int fallback)
        => c.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i : fallback;

    private static int? GetIntOrNull(IReadOnlyDictionary<string, string> c, string key)
        => c.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i : null;

    private static List<TimeOnly> ParseTimes(string? raw)
    {
        var result = new List<TimeOnly>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (var part in raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TimeOnly.TryParseExact(part.Trim(), "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var t))
            {
                result.Add(t);
            }
        }
        return result;
    }
}

/// <summary>App-pool process model and identity settings.</summary>
public sealed record KrakenIisAppPool
{
    public string Name { get; init; } = "";
    public string RuntimeVersion { get; init; } = "v4.0";
    public string PipelineMode { get; init; } = "Integrated";
    public bool Enable32Bit { get; init; }
    public bool LoadUserProfile { get; init; }
    public string IdentityType { get; init; } = "ApplicationPoolIdentity";
    public string? Username { get; init; }
    public string? Password { get; init; }
    public int IdleTimeoutMinutes { get; init; } = 20;
    public string StartMode { get; init; } = "OnDemand";
    public int QueueLength { get; init; } = 1000;
}

/// <summary>App-pool recycling configuration.</summary>
public sealed record KrakenIisRecycle
{
    public int RegularIntervalMinutes { get; init; } = 1740;
    public int? PrivateMemoryLimitKB { get; init; }
    public int? VirtualMemoryLimitKB { get; init; }
    public int? RequestLimit { get; init; }
    public IReadOnlyList<TimeOnly> SpecificTimes { get; init; } = [];
    public bool LogEventTime { get; init; } = true;
    public bool LogEventMemory { get; init; } = true;
    public bool LogEventRequests { get; init; } = true;
    public bool LogEventSchedule { get; init; } = true;
    public bool LogEventConfig { get; init; } = true;
    public bool LogEventIsapi { get; init; } = true;
    public bool LogEventOnDemand { get; init; } = true;
}

/// <summary>Rapid-fail protection configuration.</summary>
public sealed record KrakenIisRapidFail
{
    public bool Enabled { get; init; } = true;
    public int MaxCrashesPerInterval { get; init; } = 5;
    public int IntervalMinutes { get; init; } = 5;
}

/// <summary>
/// Site-level authentication module toggles. Each flag corresponds to an IIS
/// module: <c>anonymousAuthentication</c>, <c>basicAuthentication</c>, and
/// <c>windowsAuthentication</c>. Multiple modules may be enabled simultaneously —
/// IIS will accept any matching credential. Defaults mirror a freshly-created
/// IIS site (anonymous on, basic + windows off).
/// </summary>
public sealed record KrakenIisAuthentication
{
    public bool AnonymousEnabled { get; init; } = true;
    public bool BasicEnabled { get; init; }
    public bool WindowsEnabled { get; init; }
}

/// <summary>Deploy strategy: in-place vs versioned atomic-swap.</summary>
public sealed record KrakenIisDeploy
{
    public string Mode { get; init; } = "AtomicSwap";
    public int KeepVersions { get; init; } = 5;
    public bool DrainModeRecycle { get; init; } = true;

    public bool IsAtomicSwap => Mode.Equals("AtomicSwap", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Post-deploy HTTP health probe configuration.</summary>
public sealed record KrakenIisHealthCheck
{
    public required string Url { get; init; }
    public int ExpectedStatus { get; init; } = 200;
    public int TimeoutSeconds { get; init; } = 30;
    public int RetryAttempts { get; init; } = 5;
    public int RetryDelaySeconds { get; init; } = 3;
    public string? ExpectedBodyContains { get; init; }
}
