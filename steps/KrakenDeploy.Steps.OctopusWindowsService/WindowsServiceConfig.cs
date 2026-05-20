namespace KrakenDeploy.Steps.OctopusWindowsService;

/// <summary>
/// Step config keys for an <c>Octopus.WindowsService</c> step, mirroring
/// Octopus's <c>Octopus.Action.WindowsService.*</c> namespace exactly. Used
/// both to read the property bag during parsing and to round-trip back to an
/// Octopus deploymentprocess JSON later.
/// </summary>
public static class WindowsServiceConfigKeys
{
    private const string Prefix    = "Octopus.Action.WindowsService.";
    private const string PkgPrefix = "Octopus.Action.Package.";

    public const string ServiceName           = Prefix + "ServiceName";
    public const string DisplayName           = Prefix + "DisplayName";
    public const string Description           = Prefix + "Description";
    public const string ExecutablePath        = Prefix + "ExecutablePath";
    public const string Arguments             = Prefix + "Arguments";
    public const string StartMode             = Prefix + "StartMode";
    public const string DesiredStatus         = Prefix + "DesiredStatus";
    public const string ServiceAccount        = Prefix + "ServiceAccount";
    public const string CustomAccountName     = Prefix + "CustomAccountName";
    public const string CustomAccountPassword = Prefix + "CustomAccountPassword";
    public const string Dependencies          = Prefix + "Dependencies";

    public const string CustomInstallationDirectory = PkgPrefix + "CustomInstallationDirectory";
}

/// <summary>
/// Parsed, Octostache-substituted view of an <c>Octopus.WindowsService</c>
/// step's config. Mirrors what Octopus's
/// <a href="https://octopus.com/docs/deployments/windows/windows-services">public docs</a>
/// document. Built from a flat string→string <see cref="DeploymentStepPlan.Config"/>
/// bag via <see cref="Parse"/>.
/// </summary>
public sealed record WindowsServiceConfig
{
    /// <summary>Required. The Windows service short name (passed to <c>sc.exe create</c>).</summary>
    public required string ServiceName { get; init; }

    /// <summary>Display name shown in <c>services.msc</c>. Defaults to <see cref="ServiceName"/>.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Optional human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Required. Path to the service executable. If relative, it resolves against
    /// <see cref="InstallRoot"/>. Octostache substitution is applied during parse.
    /// </summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Command-line arguments passed to the executable.</summary>
    public string? Arguments { get; init; }

    /// <summary>
    /// Service start mode. Normalised to one of:
    /// <c>auto</c> / <c>delayed-auto</c> / <c>manual</c> / <c>disabled</c> / <c>unchanged</c>.
    /// Accepts Octopus's UI labels (<c>Automatic</c>, <c>Automatic (delayed)</c>) and
    /// the canonical token form interchangeably on read.
    /// </summary>
    public required string StartMode { get; init; }

    /// <summary>
    /// Desired post-deploy state. Normalised to <c>Running</c> or <c>Stopped</c>.
    /// </summary>
    public required string DesiredStatus { get; init; }

    /// <summary>
    /// Account the service runs under. One of <c>LocalSystem</c> / <c>LocalService</c>
    /// / <c>NetworkService</c> / <c>_CUSTOM</c>. When <c>_CUSTOM</c>, see
    /// <see cref="CustomAccountName"/> and <see cref="CustomAccountPassword"/>.
    /// </summary>
    public required string ServiceAccount { get; init; }

    /// <summary>
    /// Required when <see cref="ServiceAccount"/> is <c>_CUSTOM</c>. Typically
    /// <c>DOMAIN\user</c> or <c>.\user</c> for a local account, or
    /// <c>DOMAIN\user$</c> for a Managed Service Account.
    /// </summary>
    public string? CustomAccountName { get; init; }

    /// <summary>
    /// Required when <see cref="ServiceAccount"/> is <c>_CUSTOM</c> unless the
    /// account is an MSA (in which case it is left blank). Sensitive — the value
    /// is also subject to the Octopus sensitive-envelope round-trip on import; if
    /// the source export only carried the envelope (no real password), the value
    /// is left <c>null</c> and a parse warning is emitted.
    /// </summary>
    public string? CustomAccountPassword { get; init; }

    /// <summary>
    /// Dependency service short names. Octopus stores this as a single string of
    /// names separated by forward slashes (e.g. <c>LanmanWorkstation/TCPIP</c>);
    /// we split on both <c>/</c> and <c>,</c> for tolerance.
    /// </summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>
    /// Where the package contents live on disk. If
    /// <c>Octopus.Action.Package.CustomInstallationDirectory</c> is set, that —
    /// Octostache-evaluated. Otherwise the agent's package <c>ExtractDir</c>.
    /// </summary>
    public required string InstallRoot { get; init; }

    /// <summary>
    /// Per-value parse warnings collected during <see cref="Parse"/>. Surfaced
    /// through the handler so the operator sees them in the deploy log.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Builds a <see cref="WindowsServiceConfig"/> from a flat property bag.
    /// </summary>
    /// <param name="config">
    /// The step's config (<c>Octopus.Action.WindowsService.*</c> + <c>Octopus.Action.Package.*</c>).
    /// </param>
    /// <param name="octostache">Octostache evaluator (uses deployment variables).</param>
    /// <param name="fallbackInstallRoot">
    /// Used as <see cref="InstallRoot"/> when no <c>CustomInstallationDirectory</c>
    /// is set in the config — typically the package's <c>ExtractDir</c>.
    /// </param>
    public static WindowsServiceConfig Parse(
        IReadOnlyDictionary<string, string> config,
        Func<string, string> octostache,
        string fallbackInstallRoot)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(octostache);
        ArgumentNullException.ThrowIfNull(fallbackInstallRoot);

        var warnings = new List<string>();

        var serviceName = SubstituteOrEmpty(config, WindowsServiceConfigKeys.ServiceName, octostache);
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new InvalidOperationException(
                $"Octopus.WindowsService config is missing required key '{WindowsServiceConfigKeys.ServiceName}'.");
        }

        var executablePath = SubstituteOrEmpty(config, WindowsServiceConfigKeys.ExecutablePath, octostache);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException(
                $"Octopus.WindowsService config is missing required key '{WindowsServiceConfigKeys.ExecutablePath}'.");
        }

        var displayName = SubstituteOrEmpty(config, WindowsServiceConfigKeys.DisplayName, octostache);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = serviceName;
        }

        var startMode = NormaliseStartMode(
            SubstituteOrEmpty(config, WindowsServiceConfigKeys.StartMode, octostache),
            warnings);
        var desiredStatus = NormaliseDesiredStatus(
            SubstituteOrEmpty(config, WindowsServiceConfigKeys.DesiredStatus, octostache),
            warnings);
        var serviceAccount = NormaliseServiceAccount(
            SubstituteOrEmpty(config, WindowsServiceConfigKeys.ServiceAccount, octostache),
            warnings);

        var customAccountName     = SubstituteOrEmpty(config, WindowsServiceConfigKeys.CustomAccountName, octostache);
        var customAccountPassword = SubstituteOrEmpty(config, WindowsServiceConfigKeys.CustomAccountPassword, octostache);

        if (serviceAccount == "_CUSTOM" && string.IsNullOrWhiteSpace(customAccountName))
        {
            throw new InvalidOperationException(
                "Octopus.WindowsService config sets ServiceAccount=_CUSTOM but Octopus.Action.WindowsService.CustomAccountName is empty.");
        }

        if (LooksLikeSensitiveEnvelope(customAccountPassword))
        {
            warnings.Add(
                "Octopus.Action.WindowsService.CustomAccountPassword is a sensitive-value envelope; "
                + "the actual password is not present in the export. Bind it to a Kraken deployment variable.");
            customAccountPassword = null;
        }

        var description  = SubstituteOrEmpty(config, WindowsServiceConfigKeys.Description, octostache);
        var arguments    = SubstituteOrEmpty(config, WindowsServiceConfigKeys.Arguments, octostache);
        var dependencies = ParseDependencies(
            SubstituteOrEmpty(config, WindowsServiceConfigKeys.Dependencies, octostache));

        var customInstallDir = SubstituteOrEmpty(
            config, WindowsServiceConfigKeys.CustomInstallationDirectory, octostache);
        var installRoot = string.IsNullOrWhiteSpace(customInstallDir)
            ? fallbackInstallRoot
            : customInstallDir;

        return new WindowsServiceConfig
        {
            ServiceName           = serviceName,
            DisplayName           = displayName,
            Description           = string.IsNullOrWhiteSpace(description) ? null : description,
            ExecutablePath        = executablePath,
            Arguments             = string.IsNullOrWhiteSpace(arguments) ? null : arguments,
            StartMode             = startMode,
            DesiredStatus         = desiredStatus,
            ServiceAccount        = serviceAccount,
            CustomAccountName     = string.IsNullOrWhiteSpace(customAccountName) ? null : customAccountName,
            CustomAccountPassword = string.IsNullOrWhiteSpace(customAccountPassword) ? null : customAccountPassword,
            Dependencies          = dependencies,
            InstallRoot           = installRoot,
            Warnings              = warnings,
        };
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string SubstituteOrEmpty(
        IReadOnlyDictionary<string, string> config, string key, Func<string, string> octostache)
    {
        if (!config.TryGetValue(key, out var raw) || string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }
        return octostache(raw);
    }

    private static bool LooksLikeSensitiveEnvelope(string value)
        => !string.IsNullOrEmpty(value)
        && value.Contains("\"HasValue\"", StringComparison.Ordinal)
        && value.Contains("\"NewValue\"", StringComparison.Ordinal);

    /// <summary>
    /// Normalises Octopus's StartMode values into the tokens accepted by
    /// <c>sc.exe start=</c>: <c>auto</c>, <c>delayed-auto</c>, <c>demand</c>,
    /// <c>disabled</c>, <c>unchanged</c>.
    /// Accepts the human-readable Octopus UI labels ("Automatic", "Automatic (delayed)",
    /// "Manual", "Disabled", "Unchanged") AND the canonical token form
    /// ("auto"/"delayed-auto"/"manual"/"disabled"/"unchanged") AND mixed casing.
    /// </summary>
    private static string NormaliseStartMode(string raw, List<string> warnings)
    {
        var v = raw.Trim();
        if (string.IsNullOrEmpty(v))
        {
            return "auto"; // sensible default if the property is missing
        }
        return v.ToLowerInvariant() switch
        {
            "auto" or "automatic"                                => "auto",
            "delayed-auto" or "automatic (delayed)" or "delayed" => "delayed-auto",
            "manual" or "demand"                                 => "demand",
            "disabled"                                           => "disabled",
            "unchanged"                                          => "unchanged",
            _ => Warn(warnings,
                $"Unrecognised StartMode '{raw}' — defaulting to 'auto'.", "auto"),
        };

        static string Warn(List<string> warnings, string msg, string fallback)
        {
            warnings.Add(msg);
            return fallback;
        }
    }

    private static string NormaliseDesiredStatus(string raw, List<string> warnings)
    {
        var v = raw.Trim();
        if (string.IsNullOrEmpty(v))
        {
            return "Running";
        }
        return v.ToLowerInvariant() switch
        {
            "running" or "started" => "Running",
            "stopped"              => "Stopped",
            _ => Warn(warnings,
                $"Unrecognised DesiredStatus '{raw}' — defaulting to 'Running'.", "Running"),
        };

        static string Warn(List<string> warnings, string msg, string fallback)
        {
            warnings.Add(msg);
            return fallback;
        }
    }

    private static string NormaliseServiceAccount(string raw, List<string> warnings)
    {
        var v = raw.Trim();
        if (string.IsNullOrEmpty(v))
        {
            return "LocalSystem";
        }
        return v switch
        {
            "LocalSystem" or "LocalService" or "NetworkService" or "_CUSTOM" => v,
            _ => Warn(warnings,
                $"Unrecognised ServiceAccount '{raw}' — defaulting to 'LocalSystem'.", "LocalSystem"),
        };

        static string Warn(List<string> warnings, string msg, string fallback)
        {
            warnings.Add(msg);
            return fallback;
        }
    }

    private static IReadOnlyList<string> ParseDependencies(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        return [.. raw
            .Split(['/', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}
