namespace KrakenDeploy.Contracts.Steps;

/// <summary>
/// Registry of <see cref="StepUiSchema"/> definitions for every built-in
/// step type Kraken supports. Built once at first access and cached.
/// Future <c>.kdeploy-step</c> packages (Phase D) will plug schemas in via
/// a similar but DI-backed registry; this static one covers everything
/// that ships in the agent today.
/// </summary>
public static class BuiltInStepSchemas
{
    private static readonly Lazy<Dictionary<string, StepUiSchema>> _byStepType =
        new(BuildAll, isThreadSafe: true);

    /// <summary>
    /// Returns the schema for <paramref name="stepType"/>, or <c>null</c> if
    /// no built-in schema exists. Case-insensitive on the step-type key.
    /// </summary>
    public static StepUiSchema? GetForStepType(string stepType)
    {
        ArgumentNullException.ThrowIfNull(stepType);
        return _byStepType.Value.GetValueOrDefault(stepType.ToLowerInvariant());
    }

    /// <summary>
    /// All built-in step type identifiers (lower-cased). Useful for tests
    /// asserting the registry covers an expected set.
    /// </summary>
    public static IReadOnlyCollection<string> RegisteredStepTypes =>
        _byStepType.Value.Keys;

    private static Dictionary<string, StepUiSchema> BuildAll()
    {
        var map = new Dictionary<string, StepUiSchema>(StringComparer.Ordinal);
        Register(map, "Kraken.IIS",
            StepUiSchemaBuilder.FromType<KrakenIisStepSchemaShape>());
        Register(map, "Octopus.IIS",
            StepUiSchemaBuilder.FromType<OctopusIisStepSchemaShape>());
        Register(map, "Octopus.TentaclePackage",
            StepUiSchemaBuilder.FromType<OctopusTentaclePackageStepSchemaShape>());
        Register(map, "Kraken.Script",
            StepUiSchemaBuilder.FromType<KrakenScriptStepSchemaShape>());
        Register(map, "Octopus.Script",
            StepUiSchemaBuilder.FromType<KrakenScriptStepSchemaShape>());
        Register(map, "Octopus.SubstituteVariables",
            StepUiSchemaBuilder.FromType<OctopusSubstituteVariablesStepSchemaShape>());
        Register(map, "Octopus.FileTransform",
            StepUiSchemaBuilder.FromType<OctopusFileTransformStepSchemaShape>());
        Register(map, "Octopus.Manual",
            StepUiSchemaBuilder.FromType<OctopusManualStepSchemaShape>());
        return map;
    }

    private static void Register(
        Dictionary<string, StepUiSchema> map, string stepType, StepUiSchema schema)
        => map[stepType.ToLowerInvariant()] = schema;
}

// ── Per-step-type schema shapes ──────────────────────────────────────────────
//
// Each shape POCO drives a StepUiSchema via reflection on the [StepUiSchemaRoot],
// [StepUiGroup], [StepUiField], [StepUiEnum], and [StepUiVisibleWhen] attributes
// (Phase C-2 authoring API). The C# property bodies don't matter — only the
// attribute metadata is consumed. Key strings here are the dotted config-bag
// keys that land in DeploymentStep.Config; canonical constant references are
// noted in comments.

// ── Kraken.IIS ─────────────────────────────────────────────────────────────

/// <summary>
/// Schema shape for the <c>Kraken.IIS</c> step type — the Kraken-native superset
/// of Octopus.IIS that drives the full IIS configuration editor.
/// </summary>
[StepUiSchemaRoot(Id = "kraken.iis", Title = "Deploy to IIS",
    Version = "1.0.0", Description = "Configure an IIS site, app pool, bindings, and deploy a package payload.")]
[StepUiGroup("general", "General")]
[StepUiGroup("app-pool", "App Pool")]
[StepUiGroup("recycling", "Recycling", Collapsed = true)]
[StepUiGroup("rapid-fail", "Rapid-Fail Protection", Collapsed = true)]
[StepUiGroup("auth", "Authentication")]
[StepUiGroup("bindings", "Bindings")]
[StepUiGroup("preload", "Application Init / Preload", Collapsed = true)]
[StepUiGroup("deploy", "Deploy Strategy")]
[StepUiGroup("health", "Health Probe", Collapsed = true)]
internal sealed class KrakenIisStepSchemaShape
{
    [StepUiField(Key = KrakenIisConfigKeys.SiteName,
        Widget = StepUiWidgets.Text, Label = "Site name",
        Group = "general", Required = true)]
    public string SiteName { get; set; } = "";

    [StepUiField(Key = KrakenIisConfigKeys.WebRoot,
        Widget = StepUiWidgets.Text, Label = "Web root",
        Group = "general", Required = true,
        HelpText = "Base directory for the site. In atomic-swap mode versioned subdirs are created underneath.")]
    public string WebRoot { get; set; } = "";

    [StepUiField(Key = KrakenIisConfigKeys.AppPath,
        Widget = StepUiWidgets.Text, Label = "App path",
        Group = "general", Default = "/")]
    public string AppPath { get; set; } = "/";

    // ── App Pool ────────────────────────────────────────────────────────────

    [StepUiField(Key = KrakenIisConfigKeys.AppPoolName,
        Widget = StepUiWidgets.Text, Label = "Name",
        Group = "app-pool",
        HelpText = "Defaults to the site name when blank.")]
    public string AppPoolName { get; set; } = "";

    [StepUiField(Key = KrakenIisConfigKeys.AppPoolRuntimeVersion,
        Widget = StepUiWidgets.Select, Label = ".NET runtime version",
        Group = "app-pool", Default = "v4.0")]
    [StepUiEnum("v4.0", "v4.0")]
    [StepUiEnum("v2.0", "v2.0")]
    [StepUiEnum("",     "No Managed Code")]
    public string AppPoolRuntimeVersion { get; set; } = "v4.0";

    [StepUiField(Key = KrakenIisConfigKeys.AppPoolPipelineMode,
        Widget = StepUiWidgets.Select, Label = "Pipeline mode",
        Group = "app-pool", Default = "Integrated")]
    [StepUiEnum("Integrated", "Integrated")]
    [StepUiEnum("Classic",    "Classic")]
    public string AppPoolPipelineMode { get; set; } = "Integrated";

    [StepUiField(Key = KrakenIisConfigKeys.AppPoolEnable32Bit,
        Widget = StepUiWidgets.Checkbox, Label = "Enable 32-bit applications",
        Group = "app-pool", Default = "false")]
    public bool AppPoolEnable32Bit { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.AppPoolLoadUserProfile,
        Widget = StepUiWidgets.Checkbox, Label = "Load user profile",
        Group = "app-pool", Default = "false")]
    public bool AppPoolLoadUserProfile { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.AppPoolIdentityType,
        Widget = StepUiWidgets.Select, Label = "Identity",
        Group = "app-pool", Default = "ApplicationPoolIdentity")]
    [StepUiEnum("ApplicationPoolIdentity", "ApplicationPoolIdentity")]
    [StepUiEnum("LocalSystem",             "LocalSystem")]
    [StepUiEnum("LocalService",            "LocalService")]
    [StepUiEnum("NetworkService",          "NetworkService")]
    [StepUiEnum("SpecificUser",            "Specific user")]
    public string AppPoolIdentityType { get; set; } = "ApplicationPoolIdentity";

    [StepUiField(Key = KrakenIisConfigKeys.AppPoolUsername,
        Widget = StepUiWidgets.Text, Label = "Username",
        Group = "app-pool")]
    [StepUiVisibleWhen(Field = KrakenIisConfigKeys.AppPoolIdentityType,
        Operator = "equals", Value = "SpecificUser")]
    public string AppPoolUsername { get; set; } = "";

    [StepUiField(Key = KrakenIisConfigKeys.AppPoolPassword,
        Widget = StepUiWidgets.Sensitive, Label = "Password",
        Group = "app-pool")]
    [StepUiVisibleWhen(Field = KrakenIisConfigKeys.AppPoolIdentityType,
        Operator = "equals", Value = "SpecificUser")]
    public string AppPoolPassword { get; set; } = "";

    [StepUiField(Key = KrakenIisConfigKeys.AppPoolIdleTimeoutMin,
        Widget = StepUiWidgets.NumberInput, Label = "Idle timeout (minutes)",
        Group = "app-pool", Default = "20", Min = 0)]
    public int AppPoolIdleTimeoutMinutes { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.AppPoolStartMode,
        Widget = StepUiWidgets.Select, Label = "Start mode",
        Group = "app-pool", Default = "OnDemand")]
    [StepUiEnum("OnDemand",      "On demand")]
    [StepUiEnum("AlwaysRunning", "Always running")]
    public string AppPoolStartMode { get; set; } = "OnDemand";

    [StepUiField(Key = KrakenIisConfigKeys.AppPoolQueueLength,
        Widget = StepUiWidgets.NumberInput, Label = "Queue length",
        Group = "app-pool", Default = "1000", Min = 10, Max = 65535)]
    public int AppPoolQueueLength { get; set; }

    // ── Recycling ───────────────────────────────────────────────────────────

    [StepUiField(Key = KrakenIisConfigKeys.RecycleRegularInterval,
        Widget = StepUiWidgets.NumberInput, Label = "Regular interval (minutes)",
        Group = "recycling", Default = "1740", Min = 0,
        HelpText = "0 disables the regular interval recycle.")]
    public int RecycleRegularInterval { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.RecyclePrivateMemoryKB,
        Widget = StepUiWidgets.NumberInput, Label = "Private memory limit (KB)",
        Group = "recycling", Min = 0)]
    public int? RecyclePrivateMemoryLimitKB { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.RecycleSpecificTimes,
        Widget = StepUiWidgets.Text, Label = "Specific times (semicolon-separated HH:mm)",
        Group = "recycling", Placeholder = "03:00; 15:00")]
    public string RecycleSpecificTimes { get; set; } = "";

    // ── Rapid-Fail ──────────────────────────────────────────────────────────

    [StepUiField(Key = KrakenIisConfigKeys.RapidFailEnabled,
        Widget = StepUiWidgets.Checkbox, Label = "Enable rapid-fail protection",
        Group = "rapid-fail", Default = "true")]
    public bool RapidFailEnabled { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.RapidFailMaxCrashes,
        Widget = StepUiWidgets.NumberInput, Label = "Max crashes per interval",
        Group = "rapid-fail", Default = "5", Min = 1)]
    [StepUiVisibleWhen(Field = KrakenIisConfigKeys.RapidFailEnabled,
        Operator = "truthy")]
    public int RapidFailMaxCrashesPerInterval { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.RapidFailIntervalMinutes,
        Widget = StepUiWidgets.NumberInput, Label = "Interval (minutes)",
        Group = "rapid-fail", Default = "5", Min = 1)]
    [StepUiVisibleWhen(Field = KrakenIisConfigKeys.RapidFailEnabled,
        Operator = "truthy")]
    public int RapidFailIntervalMinutes { get; set; }

    // ── Authentication ──────────────────────────────────────────────────────

    [StepUiField(Key = KrakenIisConfigKeys.AuthenticationAnonymousEnabled,
        Widget = StepUiWidgets.Checkbox, Label = "Anonymous authentication",
        Group = "auth", Default = "true")]
    public bool AuthAnonymous { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.AuthenticationBasicEnabled,
        Widget = StepUiWidgets.Checkbox, Label = "Basic authentication",
        Group = "auth", Default = "false",
        HelpText = "Requires the IIS Basic Authentication sub-feature to be installed on the host.")]
    public bool AuthBasic { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.AuthenticationWindowsEnabled,
        Widget = StepUiWidgets.Checkbox, Label = "Windows authentication",
        Group = "auth", Default = "false",
        HelpText = "Requires the IIS Windows Authentication sub-feature to be installed on the host.")]
    public bool AuthWindows { get; set; }

    // ── Bindings ────────────────────────────────────────────────────────────

    [StepUiField(Key = KrakenIisConfigKeys.Bindings,
        Widget = StepUiWidgets.Textarea, Label = "Bindings",
        Group = "bindings",
        HelpText = "Newline-separated. Per line: protocol|ipAddress|port|host|certThumbprint|certStore|sniRequired|sslFlags. "
                 + "Example: http|*|80||||false|0  or  https|*|443|app.example.com|ABCDEF|My|true|1",
        Placeholder = "http|*|80||||false|0")]
    public string Bindings { get; set; } = "";

    // ── Preload ─────────────────────────────────────────────────────────────

    [StepUiField(Key = KrakenIisConfigKeys.PreloadEnabled,
        Widget = StepUiWidgets.Checkbox, Label = "Preload enabled",
        Group = "preload", Default = "false")]
    public bool PreloadEnabled { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.AlwaysRunning,
        Widget = StepUiWidgets.Checkbox, Label = "Always-running app pool",
        Group = "preload", Default = "false")]
    public bool AlwaysRunning { get; set; }

    // ── Deploy Strategy ─────────────────────────────────────────────────────

    [StepUiField(Key = KrakenIisConfigKeys.DeployMode,
        Widget = StepUiWidgets.Select, Label = "Deploy mode",
        Group = "deploy", Default = "AtomicSwap")]
    [StepUiEnum("AtomicSwap", "Atomic swap (versioned subdirs)")]
    [StepUiEnum("InPlace",    "In-place (copy over webroot)")]
    public string DeployMode { get; set; } = "AtomicSwap";

    [StepUiField(Key = KrakenIisConfigKeys.DeployKeepVersions,
        Widget = StepUiWidgets.NumberInput, Label = "Keep N versions",
        Group = "deploy", Default = "5", Min = 0,
        HelpText = "Older versioned subdirs beyond this count are pruned after a successful swap.")]
    [StepUiVisibleWhen(Field = KrakenIisConfigKeys.DeployMode,
        Operator = "equals", Value = "AtomicSwap")]
    public int DeployKeepVersions { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.DeployDrainMode,
        Widget = StepUiWidgets.Checkbox, Label = "Drain-mode recycle",
        Group = "deploy", Default = "true",
        HelpText = "Overlapping recycle so in-flight requests aren't dropped.")]
    public bool DeployDrainModeRecycle { get; set; }

    // ── Health Probe ────────────────────────────────────────────────────────

    [StepUiField(Key = KrakenIisConfigKeys.HealthCheckUrl,
        Widget = StepUiWidgets.Text, Label = "Health probe URL",
        Group = "health",
        HelpText = "Absolute URL or path relative to the first HTTP binding. Leave blank to disable the probe.")]
    public string HealthCheckUrl { get; set; } = "";

    [StepUiField(Key = KrakenIisConfigKeys.HealthCheckExpectedStatus,
        Widget = StepUiWidgets.NumberInput, Label = "Expected status code",
        Group = "health", Default = "200", Min = 100, Max = 599)]
    [StepUiVisibleWhen(Field = KrakenIisConfigKeys.HealthCheckUrl, Operator = "truthy")]
    public int HealthCheckExpectedStatus { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.HealthCheckTimeoutSeconds,
        Widget = StepUiWidgets.NumberInput, Label = "Timeout (seconds)",
        Group = "health", Default = "30", Min = 1)]
    [StepUiVisibleWhen(Field = KrakenIisConfigKeys.HealthCheckUrl, Operator = "truthy")]
    public int HealthCheckTimeoutSeconds { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.HealthCheckRetryAttempts,
        Widget = StepUiWidgets.NumberInput, Label = "Retry attempts",
        Group = "health", Default = "5", Min = 0)]
    [StepUiVisibleWhen(Field = KrakenIisConfigKeys.HealthCheckUrl, Operator = "truthy")]
    public int HealthCheckRetryAttempts { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.HealthCheckRetryDelaySeconds,
        Widget = StepUiWidgets.NumberInput, Label = "Retry delay (seconds)",
        Group = "health", Default = "3", Min = 0)]
    [StepUiVisibleWhen(Field = KrakenIisConfigKeys.HealthCheckUrl, Operator = "truthy")]
    public int HealthCheckRetryDelaySeconds { get; set; }

    [StepUiField(Key = KrakenIisConfigKeys.HealthCheckExpectedBodyContains,
        Widget = StepUiWidgets.Text, Label = "Expected body contains",
        Group = "health",
        HelpText = "Optional substring that must appear in the response body.")]
    [StepUiVisibleWhen(Field = KrakenIisConfigKeys.HealthCheckUrl, Operator = "truthy")]
    public string HealthCheckExpectedBodyContains { get; set; } = "";
}

// ── Octopus.IIS ────────────────────────────────────────────────────────────

/// <summary>
/// Schema shape for the imported <c>Octopus.IIS</c> step type — preserves the
/// Octopus-shape key surface 1:1 so a step round-trips between Octopus exports
/// and the Kraken editor cleanly. Three deployment types via
/// <see cref="DeploymentType"/> drive conditional visibility.
/// </summary>
[StepUiSchemaRoot(Id = "octopus.iis", Title = "Deploy to IIS (Octopus shape)",
    Version = "1.0.0",
    Description = "Configures an IIS web site, sub-application, or virtual directory.")]
[StepUiGroup("type",      "Deployment Type")]
[StepUiGroup("site",      "Web Site")]
[StepUiGroup("web-app",   "Web Application")]
[StepUiGroup("vdir",      "Virtual Directory")]
[StepUiGroup("app-pool",  "App Pool")]
[StepUiGroup("auth",      "Authentication")]
[StepUiGroup("bindings",  "Bindings")]
[StepUiGroup("package",   "Package payload")]
internal sealed class OctopusIisStepSchemaShape
{
    // Octopus.Action.IISWebSite.DeploymentType (canonical: OctopusIisConfigKeys.DeploymentType)
    [StepUiField(Key = "Octopus.Action.IISWebSite.DeploymentType",
        Widget = StepUiWidgets.Select, Label = "Deployment type",
        Group = "type", Default = "webSite")]
    [StepUiEnum("webSite",          "Web site")]
    [StepUiEnum("webApplication",   "Web application")]
    [StepUiEnum("virtualDirectory", "Virtual directory")]
    public string DeploymentType { get; set; } = "webSite";

    // ── Web Site branch ─────────────────────────────────────────────────────

    [StepUiField(Key = "Octopus.Action.IISWebSite.WebSiteName",
        Widget = StepUiWidgets.Text, Label = "Site name",
        Group = "site")]
    [StepUiVisibleWhen(Field = "Octopus.Action.IISWebSite.DeploymentType",
        Operator = "equals", Value = "webSite")]
    public string WebSiteName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.IISWebSite.CreateOrUpdateWebSite",
        Widget = StepUiWidgets.Checkbox, Label = "Create or update web site",
        Group = "site", Default = "true")]
    [StepUiVisibleWhen(Field = "Octopus.Action.IISWebSite.DeploymentType",
        Operator = "equals", Value = "webSite")]
    public bool CreateOrUpdateWebSite { get; set; }

    [StepUiField(Key = "Octopus.Action.IISWebSite.StartWebSite",
        Widget = StepUiWidgets.Checkbox, Label = "Start web site",
        Group = "site", Default = "true")]
    [StepUiVisibleWhen(Field = "Octopus.Action.IISWebSite.DeploymentType",
        Operator = "equals", Value = "webSite")]
    public bool StartWebSite { get; set; }

    [StepUiField(Key = "Octopus.Action.IISWebSite.StartApplicationPool",
        Widget = StepUiWidgets.Checkbox, Label = "Start application pool",
        Group = "site", Default = "true")]
    [StepUiVisibleWhen(Field = "Octopus.Action.IISWebSite.DeploymentType",
        Operator = "equals", Value = "webSite")]
    public bool StartApplicationPool { get; set; }

    [StepUiField(Key = "Octopus.Action.IISWebSite.WebRootType",
        Widget = StepUiWidgets.Select, Label = "Web root type",
        Group = "site", Default = "packageRoot")]
    [StepUiEnum("packageRoot",      "Package root")]
    [StepUiEnum("packageDirectory", "Package subdirectory")]
    [StepUiVisibleWhen(Field = "Octopus.Action.IISWebSite.DeploymentType",
        Operator = "equals", Value = "webSite")]
    public string WebRootType { get; set; } = "packageRoot";

    // ── Web Application branch ──────────────────────────────────────────────

    [StepUiField(Key = "Octopus.Action.IISWebSite.WebApplication.WebSiteName",
        Widget = StepUiWidgets.Text, Label = "Parent site name",
        Group = "web-app")]
    [StepUiVisibleWhen(Field = "Octopus.Action.IISWebSite.DeploymentType",
        Operator = "equals", Value = "webApplication")]
    public string WebApplicationParentSite { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.IISWebSite.WebApplication.VirtualPath",
        Widget = StepUiWidgets.Text, Label = "Virtual path",
        Group = "web-app", Placeholder = "/sub")]
    [StepUiVisibleWhen(Field = "Octopus.Action.IISWebSite.DeploymentType",
        Operator = "equals", Value = "webApplication")]
    public string WebApplicationVirtualPath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.IISWebSite.WebApplication.CreateOrUpdate",
        Widget = StepUiWidgets.Checkbox, Label = "Create or update web application",
        Group = "web-app", Default = "true")]
    [StepUiVisibleWhen(Field = "Octopus.Action.IISWebSite.DeploymentType",
        Operator = "equals", Value = "webApplication")]
    public bool WebApplicationCreateOrUpdate { get; set; }

    // ── Virtual Directory branch ────────────────────────────────────────────

    [StepUiField(Key = "Octopus.Action.IISWebSite.VirtualDirectory.WebSiteName",
        Widget = StepUiWidgets.Text, Label = "Parent site name",
        Group = "vdir")]
    [StepUiVisibleWhen(Field = "Octopus.Action.IISWebSite.DeploymentType",
        Operator = "equals", Value = "virtualDirectory")]
    public string VirtualDirectoryParentSite { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.IISWebSite.VirtualDirectory.VirtualPath",
        Widget = StepUiWidgets.Text, Label = "Virtual path",
        Group = "vdir", Placeholder = "/static-content")]
    [StepUiVisibleWhen(Field = "Octopus.Action.IISWebSite.DeploymentType",
        Operator = "equals", Value = "virtualDirectory")]
    public string VirtualDirectoryVirtualPath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.IISWebSite.VirtualDirectory.CreateOrUpdate",
        Widget = StepUiWidgets.Checkbox, Label = "Create or update virtual directory",
        Group = "vdir", Default = "true")]
    [StepUiVisibleWhen(Field = "Octopus.Action.IISWebSite.DeploymentType",
        Operator = "equals", Value = "virtualDirectory")]
    public bool VirtualDirectoryCreateOrUpdate { get; set; }

    // ── App Pool (shared between webSite and webApplication) ────────────────

    [StepUiField(Key = "Octopus.Action.IISWebSite.ApplicationPoolName",
        Widget = StepUiWidgets.Text, Label = "App pool name",
        Group = "app-pool")]
    public string ApplicationPoolName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.IISWebSite.ApplicationPoolFrameworkVersion",
        Widget = StepUiWidgets.Select, Label = ".NET framework version",
        Group = "app-pool", Default = "v4.0")]
    [StepUiEnum("v4.0", "v4.0")]
    [StepUiEnum("v2.0", "v2.0")]
    [StepUiEnum("",     "No Managed Code")]
    public string ApplicationPoolFrameworkVersion { get; set; } = "v4.0";

    [StepUiField(Key = "Octopus.Action.IISWebSite.ApplicationPoolIdentityType",
        Widget = StepUiWidgets.Select, Label = "Identity",
        Group = "app-pool", Default = "ApplicationPoolIdentity")]
    [StepUiEnum("ApplicationPoolIdentity", "ApplicationPoolIdentity")]
    [StepUiEnum("LocalSystem",             "LocalSystem")]
    [StepUiEnum("LocalService",            "LocalService")]
    [StepUiEnum("NetworkService",          "NetworkService")]
    [StepUiEnum("SpecificUser",            "Specific user")]
    public string ApplicationPoolIdentityType { get; set; } = "ApplicationPoolIdentity";

    [StepUiField(Key = "Octopus.Action.IISWebSite.ApplicationPoolUsername",
        Widget = StepUiWidgets.Text, Label = "Username",
        Group = "app-pool")]
    [StepUiVisibleWhen(Field = "Octopus.Action.IISWebSite.ApplicationPoolIdentityType",
        Operator = "equals", Value = "SpecificUser")]
    public string ApplicationPoolUsername { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.IISWebSite.ApplicationPoolPassword",
        Widget = StepUiWidgets.Sensitive, Label = "Password",
        Group = "app-pool")]
    [StepUiVisibleWhen(Field = "Octopus.Action.IISWebSite.ApplicationPoolIdentityType",
        Operator = "equals", Value = "SpecificUser")]
    public string ApplicationPoolPassword { get; set; } = "";

    // ── Authentication ──────────────────────────────────────────────────────

    [StepUiField(Key = "Octopus.Action.IISWebSite.EnableAnonymousAuthentication",
        Widget = StepUiWidgets.Checkbox, Label = "Anonymous",
        Group = "auth")]
    public bool EnableAnonymousAuthentication { get; set; }

    [StepUiField(Key = "Octopus.Action.IISWebSite.EnableBasicAuthentication",
        Widget = StepUiWidgets.Checkbox, Label = "Basic",
        Group = "auth")]
    public bool EnableBasicAuthentication { get; set; }

    [StepUiField(Key = "Octopus.Action.IISWebSite.EnableWindowsAuthentication",
        Widget = StepUiWidgets.Checkbox, Label = "Windows",
        Group = "auth")]
    public bool EnableWindowsAuthentication { get; set; }

    // ── Bindings ────────────────────────────────────────────────────────────

    [StepUiField(Key = "Octopus.Action.IISWebSite.Bindings",
        Widget = StepUiWidgets.JsonEditor, Label = "Bindings (JSON array)",
        Group = "bindings",
        HelpText = "JSON array of binding objects with keys "
                 + "protocol/ipAddress/port/host/thumbprint/certificateVariable/requireSni/enabled.")]
    public string Bindings { get; set; } = "[]";

    // ── Package payload ─────────────────────────────────────────────────────

    [StepUiField(Key = "Octopus.Action.Package.CustomInstallationDirectory",
        Widget = StepUiWidgets.Text, Label = "Custom installation directory",
        Group = "package",
        HelpText = "Where the package is extracted. Blank uses the agent's per-step staging dir.")]
    public string CustomInstallationDirectory { get; set; } = "";
}

// ── Octopus.TentaclePackage ────────────────────────────────────────────────

/// <summary>
/// Schema shape for the imported <c>Octopus.TentaclePackage</c> step. Three
/// optional features (CustomDirectory, ConfigurationVariables,
/// ConfigurationTransforms) are surfaced as standalone checkboxes whose
/// dependent fields gate via visibleWhen — Octopus's <c>EnabledFeatures</c>
/// comma-separated string is constructed by the handler from these toggles
/// (the bag still carries the verbatim feature list for round-trip).
/// </summary>
[StepUiSchemaRoot(Id = "octopus.tentaclepackage",
    Title = "Deploy a package (Octopus.TentaclePackage)",
    Version = "1.0.0",
    Description = "Extracts a package and optionally applies the standard Octopus configuration features.")]
[StepUiGroup("general",       "General")]
[StepUiGroup("custom-dir",    "Custom installation directory")]
[StepUiGroup("config-vars",   "Configuration variables (XML)")]
[StepUiGroup("config-xforms", "Configuration transforms (XDT)")]
internal sealed class OctopusTentaclePackageStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Package.PackageId",
        Widget = StepUiWidgets.PackageRef, Label = "Package",
        Group = "general", Required = true)]
    public string PackageId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Package.FeedId",
        Widget = StepUiWidgets.Text, Label = "Feed id",
        Group = "general", Default = "feeds-builtin",
        HelpText = "Kraken only models the built-in feed today; the value is preserved verbatim for round-trip.")]
    public string FeedId { get; set; } = "feeds-builtin";

    [StepUiField(Key = "Octopus.Action.Package.DownloadOnTentacle",
        Widget = StepUiWidgets.Checkbox, Label = "Download on the target",
        Group = "general", Default = "false")]
    public bool DownloadOnTentacle { get; set; }

    [StepUiField(Key = "Octopus.Action.EnabledFeatures",
        Widget = StepUiWidgets.Text, Label = "Enabled features (comma-separated)",
        Group = "general",
        HelpText = "Octopus feature ids (e.g. Octopus.Features.CustomDirectory). "
                 + "Per-feature config blocks below appear when the matching feature is listed.")]
    public string EnabledFeatures { get; set; } = "";

    // ── CustomDirectory ─────────────────────────────────────────────────────

    [StepUiField(Key = "Octopus.Action.Package.CustomInstallationDirectory",
        Widget = StepUiWidgets.Text, Label = "Installation directory",
        Group = "custom-dir",
        HelpText = "Where the extracted package is copied. Required when Octopus.Features.CustomDirectory is enabled.")]
    public string CustomInstallationDirectory { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Package.CustomInstallationDirectoryShouldBePurgedBeforeDeployment",
        Widget = StepUiWidgets.Checkbox, Label = "Purge before copy",
        Group = "custom-dir", Default = "false")]
    public bool PurgeBeforeDeployment { get; set; }

    [StepUiField(Key = "Octopus.Action.Package.CustomInstallationDirectoryPurgeExclusions",
        Widget = StepUiWidgets.Textarea, Label = "Purge exclusions",
        Group = "custom-dir",
        HelpText = "Comma- or newline-separated top-level entry names to keep (e.g. App_Data).")]
    public string PurgeExclusions { get; set; } = "";

    // ── ConfigurationVariables ──────────────────────────────────────────────

    [StepUiField(Key = "Octopus.Action.Package.AutomaticallyUpdateAppSettingsAndConnectionStrings",
        Widget = StepUiWidgets.Checkbox, Label = "Automatically update appSettings + connectionStrings",
        Group = "config-vars", Default = "false")]
    public bool AutomaticallyUpdateAppSettingsAndConnectionStrings { get; set; }

    // ── ConfigurationTransforms ─────────────────────────────────────────────

    [StepUiField(Key = "Octopus.Action.Package.AutomaticallyRunConfigurationTransformationFiles",
        Widget = StepUiWidgets.Checkbox, Label = "Automatically run XDT transforms",
        Group = "config-xforms", Default = "false",
        HelpText = "Applies *.<env>.config transforms over their base file.")]
    public bool AutomaticallyRunConfigurationTransformationFiles { get; set; }

    [StepUiField(Key = "Octopus.Action.Package.AdditionalXmlConfigurationTransforms",
        Widget = StepUiWidgets.Textarea, Label = "Additional XDT transform mappings",
        Group = "config-xforms",
        HelpText = "Newline-separated. Per line: <transform-file> => <target-file>. "
                 + "(Not yet honoured at runtime; preserved for round-trip.)")]
    public string AdditionalXmlConfigurationTransforms { get; set; } = "";
}

// ── Kraken.Script / Octopus.Script ─────────────────────────────────────────

/// <summary>
/// Schema shape for inline script steps — both Kraken.Script and Octopus.Script
/// use the same Octopus-compatible key set.
/// </summary>
[StepUiSchemaRoot(Id = "kraken.script", Title = "Run a script",
    Version = "1.0.0",
    Description = "Runs an inline script in PowerShell, Bash, C#, F#, or Python.")]
[StepUiGroup("script",      "Script")]
[StepUiGroup("execution",   "Execution")]
internal sealed class KrakenScriptStepSchemaShape
{
    [StepUiField(Key = KrakenScriptConfigKeys.Syntax,
        Widget = StepUiWidgets.Select, Label = "Language",
        Group = "script", Default = "PowerShell")]
    [StepUiEnum("PowerShell", "PowerShell")]
    [StepUiEnum("Bash",       "Bash")]
    [StepUiEnum("CSharp",     "C# (dotnet script)")]
    [StepUiEnum("FSharp",     "F# (dotnet fsi)")]
    [StepUiEnum("Python",     "Python")]
    public string Syntax { get; set; } = "PowerShell";

    [StepUiField(Key = KrakenScriptConfigKeys.PowerShellEdition,
        Widget = StepUiWidgets.Select, Label = "PowerShell edition",
        Group = "script", Default = "Desktop")]
    [StepUiEnum("Desktop", "Desktop (Windows PowerShell 5.x)")]
    [StepUiEnum("Core",    "Core (PowerShell 7+ / pwsh)")]
    [StepUiVisibleWhen(Field = KrakenScriptConfigKeys.Syntax,
        Operator = "equals", Value = "PowerShell")]
    public string PowerShellEdition { get; set; } = "Desktop";

    [StepUiField(Key = KrakenScriptConfigKeys.ScriptBody,
        Widget = StepUiWidgets.Textarea, Label = "Script",
        Group = "script", Required = true,
        Placeholder = "Write-Host 'Hello from KrakenDeploy.'")]
    public string ScriptBody { get; set; } = "";

    [StepUiField(Key = KrakenScriptConfigKeys.RunOnServer,
        Widget = StepUiWidgets.Checkbox, Label = "Run on the KrakenDeploy server",
        Group = "execution", Default = "false",
        HelpText = "When off, the script runs on each deployment target. When on, it runs on the server.")]
    public bool RunOnServer { get; set; }
}

// ── Octopus.SubstituteVariables ────────────────────────────────────────────

/// <summary>
/// Schema shape for the standalone Octostache-substitute-in-files step.
/// </summary>
[StepUiSchemaRoot(Id = "octopus.substitutevariables", Title = "Substitute variables in files",
    Version = "1.0.0",
    Description = "Applies Octostache #{...} substitution to files in the extracted package.")]
internal sealed class OctopusSubstituteVariablesStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.SubstituteInFiles.TargetFiles",
        Widget = StepUiWidgets.Textarea, Label = "Target files",
        Required = true,
        HelpText = "Newline- or comma-separated glob patterns relative to the package extract directory.",
        Placeholder = "appsettings.json\nweb.config")]
    public string TargetFiles { get; set; } = "";
}

// ── Octopus.FileTransform ──────────────────────────────────────────────────

/// <summary>
/// Schema shape for the standalone JSON-config-variables step.
/// </summary>
[StepUiSchemaRoot(Id = "octopus.filetransform", Title = "Apply JSON configuration variables",
    Version = "1.0.0",
    Description = "Walks each variable name like 'A.B.C' and applies it to the JSON path A → B → C in the target files.")]
internal sealed class OctopusFileTransformStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Package.JsonConfigurationVariablesTargets",
        Widget = StepUiWidgets.Textarea, Label = "Target files",
        Required = true,
        HelpText = "Newline- or comma-separated globs (e.g. appsettings*.json).",
        Placeholder = "appsettings.json\nappsettings.*.json")]
    public string Targets { get; set; } = "";
}

// ── Octopus.Manual ─────────────────────────────────────────────────────────

/// <summary>
/// Schema shape for manual-intervention steps. Kraken auto-approves in
/// unattended mode but preserves the responsible-team + block-concurrent
/// metadata for audit + round-trip back to Octopus.
/// </summary>
[StepUiSchemaRoot(Id = "octopus.manual", Title = "Manual intervention",
    Version = "1.0.0",
    Description = "Pause for human approval. Kraken auto-approves unattended; the fields below drive Octopus's attended mode and the audit log.")]
internal sealed class OctopusManualStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Manual.Instructions",
        Widget = StepUiWidgets.Textarea, Label = "Instructions (markdown)",
        Required = true,
        HelpText = "Shown to the approver. Octostache #{...} placeholders are evaluated against the deployment variables.")]
    public string Instructions { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Manual.ResponsibleTeamIds",
        Widget = StepUiWidgets.Text, Label = "Responsible team ids",
        HelpText = "Comma- or semicolon-separated. Optional in Kraken (unattended mode bypasses team scoping).")]
    public string ResponsibleTeamIds { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Manual.BlockConcurrentDeployments",
        Widget = StepUiWidgets.Checkbox, Label = "Block concurrent deployments",
        Default = "false",
        HelpText = "Honoured by Octopus attended mode only. Kraken always runs unattended and does not gate concurrent deployments.")]
    public bool BlockConcurrentDeployments { get; set; }
}
