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
        Register(map, "Kraken.DeployPackage",
            StepUiSchemaBuilder.FromType<KrakenDeployPackageStepSchemaShape>());
        Register(map, "Kraken.Script",
            StepUiSchemaBuilder.FromType<KrakenScriptStepSchemaShape>());
        Register(map, "Octopus.Script",
            StepUiSchemaBuilder.FromType<KrakenScriptStepSchemaShape>());
        Register(map, "Octopus.SubstituteVariables",
            StepUiSchemaBuilder.FromType<OctopusSubstituteVariablesStepSchemaShape>());
        Register(map, "Octopus.JsonConfigurationVariables",
            StepUiSchemaBuilder.FromType<OctopusJsonConfigurationVariablesStepSchemaShape>());
        Register(map, "Octopus.Manual",
            StepUiSchemaBuilder.FromType<OctopusManualStepSchemaShape>());
        Register(map, "Octopus.HealthCheck",
            StepUiSchemaBuilder.FromType<OctopusHealthCheckStepSchemaShape>());
        Register(map, "Octopus.TransferPackage",
            StepUiSchemaBuilder.FromType<OctopusTransferPackageStepSchemaShape>());
        Register(map, "Octopus.DockerRun",
            StepUiSchemaBuilder.FromType<OctopusDockerRunStepSchemaShape>());
        Register(map, "Octopus.DockerStop",
            StepUiSchemaBuilder.FromType<OctopusDockerStopStepSchemaShape>());
        Register(map, "Octopus.DockerNetwork",
            StepUiSchemaBuilder.FromType<OctopusDockerNetworkStepSchemaShape>());
        Register(map, "Octopus.KubernetesDeployRawYaml",
            StepUiSchemaBuilder.FromType<KubernetesDeployRawYamlStepSchemaShape>());
        Register(map, "Octopus.KubernetesDeployContainers",
            StepUiSchemaBuilder.FromType<KubernetesDeployContainersStepSchemaShape>());
        Register(map, "Octopus.KubernetesDeployService",
            StepUiSchemaBuilder.FromType<KubernetesDeployServiceStepSchemaShape>());
        Register(map, "Octopus.KubernetesDeployIngress",
            StepUiSchemaBuilder.FromType<KubernetesDeployIngressStepSchemaShape>());
        Register(map, "Octopus.KubernetesDeployConfigMap",
            StepUiSchemaBuilder.FromType<KubernetesDeployConfigMapStepSchemaShape>());
        Register(map, "Octopus.KubernetesDeploySecret",
            StepUiSchemaBuilder.FromType<KubernetesDeploySecretStepSchemaShape>());
        Register(map, "Octopus.Kubernetes.Kustomize",
            StepUiSchemaBuilder.FromType<KubernetesKustomizeStepSchemaShape>());
        Register(map, "Octopus.HelmChartUpgrade",
            StepUiSchemaBuilder.FromType<KubernetesHelmChartUpgradeStepSchemaShape>());
        Register(map, "Octopus.KubernetesRunScript",
            StepUiSchemaBuilder.FromType<KubernetesRunScriptStepSchemaShape>());
        Register(map, "Octopus.AwsUploadS3",
            StepUiSchemaBuilder.FromType<AwsUploadS3StepSchemaShape>());
        Register(map, "Octopus.AwsCreateS3",
            StepUiSchemaBuilder.FromType<AwsCreateS3StepSchemaShape>());
        Register(map, "Octopus.AwsRunCloudFormation",
            StepUiSchemaBuilder.FromType<AwsRunCloudFormationStepSchemaShape>());
        Register(map, "Octopus.AwsApplyCloudFormationChangeSet",
            StepUiSchemaBuilder.FromType<AwsApplyChangeSetStepSchemaShape>());
        Register(map, "Octopus.AwsDeleteCloudFormation",
            StepUiSchemaBuilder.FromType<AwsDeleteCloudFormationStepSchemaShape>());
        Register(map, "aws-ecs",
            StepUiSchemaBuilder.FromType<AwsEcsDeployStepSchemaShape>());
        Register(map, "aws-ecs-update-service",
            StepUiSchemaBuilder.FromType<AwsEcsDeployStepSchemaShape>());
        Register(map, "Octopus.AwsRunScript",
            StepUiSchemaBuilder.FromType<AwsRunScriptStepSchemaShape>());
        Register(map, "Octopus.AzureWebApp",
            StepUiSchemaBuilder.FromType<AzureWebAppStepSchemaShape>());
        Register(map, "Octopus.AzureAppService",
            StepUiSchemaBuilder.FromType<AzureWebAppStepSchemaShape>());
        Register(map, "Octopus.AzurePowerShell",
            StepUiSchemaBuilder.FromType<AzurePowerShellStepSchemaShape>());
        Register(map, "Octopus.AzureResourceGroup",
            StepUiSchemaBuilder.FromType<AzureResourceGroupStepSchemaShape>());
        Register(map, "deploy-a-bicep-template",
            StepUiSchemaBuilder.FromType<AzureBicepStepSchemaShape>());
        Register(map, "Octopus.JavaArchive",
            StepUiSchemaBuilder.FromType<JavaArchiveStepSchemaShape>());
        Register(map, "Octopus.TomcatDeploy",
            StepUiSchemaBuilder.FromType<TomcatDeployStepSchemaShape>());
        Register(map, "Octopus.TomcatState",
            StepUiSchemaBuilder.FromType<TomcatStateStepSchemaShape>());
        Register(map, "Octopus.TomcatDeployCertificate",
            StepUiSchemaBuilder.FromType<TomcatCertificateStepSchemaShape>());
        Register(map, "Octopus.WildFlyDeploy",
            StepUiSchemaBuilder.FromType<WildFlyDeployStepSchemaShape>());
        Register(map, "Octopus.WildFlyState",
            StepUiSchemaBuilder.FromType<WildFlyStateStepSchemaShape>());
        Register(map, "Octopus.WildFlyCertificateDeploy",
            StepUiSchemaBuilder.FromType<WildFlyCertificateStepSchemaShape>());
        Register(map, "Octopus.JavaDeployCertificate",
            StepUiSchemaBuilder.FromType<JavaDeployCertificateStepSchemaShape>());
        Register(map, "Octopus.TerraformApply",
            StepUiSchemaBuilder.FromType<TerraformApplyStepSchemaShape>());
        Register(map, "Octopus.TerraformPlan",
            StepUiSchemaBuilder.FromType<TerraformPlanStepSchemaShape>());
        Register(map, "Octopus.TerraformDestroy",
            StepUiSchemaBuilder.FromType<TerraformDestroyStepSchemaShape>());
        Register(map, "Octopus.TerraformPlanDestroy",
            StepUiSchemaBuilder.FromType<TerraformPlanDestroyStepSchemaShape>());
        Register(map, "Octopus.Email",
            StepUiSchemaBuilder.FromType<EmailStepSchemaShape>());
        Register(map, "Octopus.Nginx",
            StepUiSchemaBuilder.FromType<NginxStepSchemaShape>());
        Register(map, "Octopus.Certificate.Import",
            StepUiSchemaBuilder.FromType<CertificateImportStepSchemaShape>());
        Register(map, "Octopus.Vhd",
            StepUiSchemaBuilder.FromType<VhdStepSchemaShape>());
        Register(map, "Kraken.RunPackageExecutable",
            StepUiSchemaBuilder.FromType<RunPackageExecutableStepSchemaShape>());
        Register(map, "Kraken.RunPackageAssembly",
            StepUiSchemaBuilder.FromType<RunPackageAssemblyStepSchemaShape>());
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

// ── Octopus.JsonConfigurationVariables ─────────────────────────────────────
// Was historically named Octopus.FileTransform in Kraken's schema; renamed to
// match what Octopus's own docs call this feature ("JSON Configuration
// Variables"). XDT (XML) transforms live on Octopus.TentaclePackage where
// Octopus puts them, not here.

/// <summary>
/// Schema shape for the standalone JSON-config-variables step.
/// </summary>
[StepUiSchemaRoot(Id = "octopus.jsonconfigurationvariables", Title = "Apply JSON configuration variables",
    Version = "1.0.0",
    Description = "Walks each variable name like 'A.B.C' and applies it to the JSON path A → B → C in the target files.")]
internal sealed class OctopusJsonConfigurationVariablesStepSchemaShape
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
/// Schema shape for manual-intervention steps. WP3 made this a REAL gate: the task
/// pauses before this step's wave runs and waits for a human to approve or reject.
/// The help text below is operator-facing and load-bearing — it previously told
/// operators Kraken auto-approves, which is no longer true.
/// </summary>
[StepUiSchemaRoot(Id = "octopus.manual", Title = "Manual intervention",
    Version = "1.0.0",
    Description = "Pause the task and wait for a human to approve or reject. The whole task pauses before this step's wave runs — no target is touched until somebody decides.")]
internal sealed class OctopusManualStepSchemaShape
{
    [StepUiField(Key = ManualInterventionConfigKeys.Instructions,
        Widget = StepUiWidgets.Textarea, Label = "Instructions (markdown)",
        Required = true,
        HelpText = "Shown to the approver. Octostache #{...} placeholders are resolved when the task pauses, so the approver reads real values rather than the template.")]
    public string Instructions { get; set; } = "";

    [StepUiField(Key = ManualInterventionConfigKeys.ResponsibleTeamIds,
        Widget = StepUiWidgets.ResponsibleTeams, Label = "Responsible teams",
        HelpText = "Leave EMPTY to let anyone in this Space holding the approve permission respond. Selections are stored as team ids, so a process imported from Octopus carries Octopus ids that resolve to nothing: the import reports them as a warning, saving the step here is refused until they are re-pointed at real teams, and a deployment that reaches the gate anyway FAILS rather than proceeding — because ignoring an unresolvable list would widen the approver set to everyone instead of narrowing it.")]
    public string ResponsibleTeamIds { get; set; } = "";

    [StepUiField(Key = ManualInterventionConfigKeys.TimeoutHours,
        Widget = StepUiWidgets.Text, Label = "Auto-fail after (hours)",
        HelpText = "Blank uses the server default (72 h). 0 waits indefinitely. On expiry the task fails exactly as if rejected, and its Failure/Always cleanup steps still run.")]
    public string TimeoutHours { get; set; } = "";

    [StepUiField(Key = ManualInterventionConfigKeys.BlockConcurrentDeployments,
        Widget = StepUiWidgets.Checkbox, Label = "Block concurrent deployments",
        Default = "false",
        HelpText = "Informational only. Kraken already serializes deployments per project + environment + tenant unconditionally, which is stronger — and a paused task keeps holding that slot until it is answered or times out.")]
    public bool BlockConcurrentDeployments { get; set; }
}

// ── Octopus.HealthCheck ───────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.healthcheck", Title = "Health Check",
    Version = "1.0.0",
    Description = "Probe an HTTP endpoint or TCP port and retry on failure.")]
[StepUiGroup("target", "Target")]
[StepUiGroup("retries", "Retries & Failure", Collapsed = true)]
internal sealed class OctopusHealthCheckStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.HealthCheck.Uri",
        Widget = StepUiWidgets.Text, Label = "URI",
        Group = "target",
        HelpText = "Full URL (http/https) or hostname for TCP probes. Mutually exclusive with Host/Protocol/Port.",
        Placeholder = "https://myapp.local/health")]
    public string Uri { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.HealthCheck.Protocol",
        Widget = StepUiWidgets.Select, Label = "Protocol",
        Group = "target", Default = "http")]
    [StepUiEnum("http", "HTTP")]
    [StepUiEnum("tcp",  "TCP")]
    public string Protocol { get; set; } = "http";

    [StepUiField(Key = "Octopus.Action.HealthCheck.Host",
        Widget = StepUiWidgets.Text, Label = "Host",
        Group = "target",
        HelpText = "Used when URI is empty. Combined with Protocol and Port.")]
    public string Host { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.HealthCheck.Port",
        Widget = StepUiWidgets.NumberInput, Label = "Port",
        Group = "target", Min = 1, Max = 65535,
        HelpText = "TCP port. Defaults to 80 for HTTP when omitted.")]
    public int Port { get; set; }

    [StepUiField(Key = "Octopus.Action.HealthCheck.ExpectedStatusCode",
        Widget = StepUiWidgets.NumberInput, Label = "Expected HTTP status code",
        Group = "target", Default = "200")]
    public int ExpectedStatusCode { get; set; } = 200;

    [StepUiField(Key = "Octopus.Action.HealthCheck.ExpectedBodyContains",
        Widget = StepUiWidgets.Text, Label = "Body must contain",
        Group = "target",
        HelpText = "Optional substring the response body must include (HTTP only).")]
    public string ExpectedBodyContains { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.HealthCheck.TimeoutSeconds",
        Widget = StepUiWidgets.NumberInput, Label = "Timeout (seconds)",
        Group = "retries", Default = "30", Min = 1)]
    public int TimeoutSeconds { get; set; } = 30;

    [StepUiField(Key = "Octopus.Action.HealthCheck.RetryAttempts",
        Widget = StepUiWidgets.NumberInput, Label = "Retry attempts",
        Group = "retries", Default = "3", Min = 1)]
    public int RetryAttempts { get; set; } = 3;

    [StepUiField(Key = "Octopus.Action.HealthCheck.RetryDelaySeconds",
        Widget = StepUiWidgets.NumberInput, Label = "Retry delay (seconds)",
        Group = "retries", Default = "5", Min = 0)]
    public int RetryDelaySeconds { get; set; } = 5;

    [StepUiField(Key = "Octopus.Action.HealthCheck.FailureAction",
        Widget = StepUiWidgets.Select, Label = "On failure",
        Group = "retries", Default = "fail")]
    [StepUiEnum("fail", "Fail the deployment")]
    [StepUiEnum("warn", "Log a warning and continue")]
    public string FailureAction { get; set; } = "fail";
}

// ── Octopus.TransferPackage ──────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.transferpackage", Title = "Transfer a Package",
    Version = "1.0.0",
    Description = "Copy or upload the deployed package to a file share or HTTP feed endpoint.")]
[StepUiGroup("destination", "Destination")]
[StepUiGroup("filter", "File Filter", Collapsed = true)]
internal sealed class OctopusTransferPackageStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.TransferPackage.DestinationType",
        Widget = StepUiWidgets.Select, Label = "Destination type",
        Group = "destination", Default = "file")]
    [StepUiEnum("file", "File system / UNC share")]
    [StepUiEnum("http", "HTTP endpoint (PUT)")]
    public string DestinationType { get; set; } = "file";

    [StepUiField(Key = "Octopus.Action.TransferPackage.DestinationPath",
        Widget = StepUiWidgets.Text, Label = "Destination path",
        Group = "destination",
        HelpText = "Local path or UNC share. Used when destination type is 'file'.",
        Placeholder = @"\\fileserver\deployments\myapp")]
    [StepUiVisibleWhen(Field = "Octopus.Action.TransferPackage.DestinationType",
        Operator = "equals", Value = "file")]
    public string DestinationPath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.TransferPackage.DestinationUrl",
        Widget = StepUiWidgets.Text, Label = "Destination URL",
        Group = "destination",
        HelpText = "HTTP(S) endpoint. Files are PUT to {url}/{filename}.",
        Placeholder = "https://feed.local/api/packages")]
    [StepUiVisibleWhen(Field = "Octopus.Action.TransferPackage.DestinationType",
        Operator = "equals", Value = "http")]
    public string DestinationUrl { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.TransferPackage.DestinationUsername",
        Widget = StepUiWidgets.Text, Label = "Username",
        Group = "destination",
        HelpText = "Optional. Basic-auth username for HTTP destinations.")]
    [StepUiVisibleWhen(Field = "Octopus.Action.TransferPackage.DestinationType",
        Operator = "equals", Value = "http")]
    public string DestinationUsername { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.TransferPackage.DestinationPassword",
        Widget = StepUiWidgets.Sensitive, Label = "Password",
        Group = "destination",
        HelpText = "Optional. Basic-auth password for HTTP destinations.")]
    [StepUiVisibleWhen(Field = "Octopus.Action.TransferPackage.DestinationType",
        Operator = "equals", Value = "http")]
    public string DestinationPassword { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.TransferPackage.FileNamePattern",
        Widget = StepUiWidgets.Text, Label = "File name pattern",
        Group = "filter", Default = "**/*",
        HelpText = "Glob pattern(s), comma- or newline-separated. Defaults to all files.")]
    public string FileNamePattern { get; set; } = "**/*";
}

// ── Kraken.DeployPackage (simplified alias over Octopus.TentaclePackage) ──

[StepUiSchemaRoot(Id = "kraken.deploypackage", Title = "Deploy a Package",
    Version = "1.0.0",
    Description = "Extract a package to a target directory. Simplified view of the Octopus.TentaclePackage handler.")]
[StepUiGroup("general", "General")]
[StepUiGroup("features", "Features", Collapsed = true)]
internal sealed class KrakenDeployPackageStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Package.PackageId",
        Widget = StepUiWidgets.PackageRef, Label = "Package",
        Group = "general", Required = true)]
    public string PackageId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Package.CustomInstallationDirectory",
        Widget = StepUiWidgets.Text, Label = "Installation directory",
        Group = "general", Required = true,
        HelpText = "Target directory where the package contents are deployed.",
        Placeholder = @"C:\Apps\MyApp")]
    public string CustomInstallationDirectory { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Package.CustomInstallationDirectoryShouldBePurgedBeforeDeployment",
        Widget = StepUiWidgets.Checkbox, Label = "Purge directory before deployment",
        Group = "general", Default = "false",
        HelpText = "Delete existing files in the installation directory before copying.")]
    public bool PurgeBeforeDeployment { get; set; }

    [StepUiField(Key = "Octopus.Action.Package.CustomInstallationDirectoryPurgeExclusions",
        Widget = StepUiWidgets.Textarea, Label = "Purge exclusions",
        Group = "general",
        HelpText = "Comma- or newline-separated entry names to keep when purging (e.g. App_Data, logs).")]
    public string PurgeExclusions { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.EnabledFeatures",
        Widget = StepUiWidgets.Text, Label = "Enabled features (advanced)",
        Group = "features", Default = "Octopus.Features.CustomDirectory",
        HelpText = "Comma-separated Octopus feature ids. CustomDirectory is set automatically when an installation directory is provided.")]
    public string EnabledFeatures { get; set; } = "Octopus.Features.CustomDirectory";

    [StepUiField(Key = "Octopus.Action.Package.AutomaticallyUpdateAppSettingsAndConnectionStrings",
        Widget = StepUiWidgets.Checkbox, Label = "Update appSettings + connectionStrings",
        Group = "features", Default = "false")]
    public bool AutomaticallyUpdateAppSettingsAndConnectionStrings { get; set; }

    [StepUiField(Key = "Octopus.Action.Package.AutomaticallyRunConfigurationTransformationFiles",
        Widget = StepUiWidgets.Checkbox, Label = "Run XDT config transforms",
        Group = "features", Default = "false")]
    public bool AutomaticallyRunConfigurationTransformationFiles { get; set; }
}

// ── Octopus.DockerRun ────────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.dockerrun", Title = "Run a Docker Container",
    Version = "1.0.0",
    Description = "Pull and run a container from a registry.")]
[StepUiGroup("image", "Image")]
[StepUiGroup("container", "Container Settings")]
[StepUiGroup("network", "Network & Ports", Collapsed = true)]
[StepUiGroup("registry", "Registry Auth", Collapsed = true)]
internal sealed class OctopusDockerRunStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Docker.Image",
        Widget = StepUiWidgets.Text, Label = "Image",
        Group = "image", Required = true,
        Placeholder = "nginx")]
    public string Image { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Docker.Tag",
        Widget = StepUiWidgets.Text, Label = "Tag",
        Group = "image", Default = "latest",
        Placeholder = "latest")]
    public string Tag { get; set; } = "latest";

    [StepUiField(Key = "Octopus.Action.Docker.ContainerName",
        Widget = StepUiWidgets.Text, Label = "Container name",
        Group = "container",
        HelpText = "Optional. Defaults to a Docker-generated name.")]
    public string ContainerName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Docker.Command",
        Widget = StepUiWidgets.Text, Label = "Command",
        Group = "container",
        HelpText = "Optional command to run inside the container.")]
    public string Command { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Docker.EntryPoint",
        Widget = StepUiWidgets.Text, Label = "Entry point override",
        Group = "container")]
    public string EntryPoint { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Docker.Detach",
        Widget = StepUiWidgets.Checkbox, Label = "Run detached",
        Group = "container", Default = "true")]
    public bool Detach { get; set; } = true;

    [StepUiField(Key = "Octopus.Action.Docker.RestartPolicy",
        Widget = StepUiWidgets.Select, Label = "Restart policy",
        Group = "container", Default = "unless-stopped")]
    [StepUiEnum("no", "no")]
    [StepUiEnum("always", "always")]
    [StepUiEnum("unless-stopped", "unless-stopped")]
    [StepUiEnum("on-failure", "on-failure")]
    public string RestartPolicy { get; set; } = "unless-stopped";

    [StepUiField(Key = "Octopus.Action.Docker.EnvVars",
        Widget = StepUiWidgets.Textarea, Label = "Environment variables",
        Group = "container",
        HelpText = "One per line: KEY=VALUE")]
    public string EnvVars { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Docker.Volumes",
        Widget = StepUiWidgets.Textarea, Label = "Volume mounts",
        Group = "container",
        HelpText = "One per line: host_path:container_path[:ro]")]
    public string Volumes { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Docker.Ports",
        Widget = StepUiWidgets.Textarea, Label = "Port mappings",
        Group = "network",
        HelpText = "One per line: host_port:container_port")]
    public string Ports { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Docker.Network",
        Widget = StepUiWidgets.Text, Label = "Network",
        Group = "network",
        HelpText = "Docker network to attach the container to.")]
    public string Network { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Docker.Labels",
        Widget = StepUiWidgets.Textarea, Label = "Labels",
        Group = "container",
        HelpText = "One per line: key=value")]
    public string Labels { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Docker.RegistryUrl",
        Widget = StepUiWidgets.Text, Label = "Registry URL",
        Group = "registry",
        HelpText = "Leave empty for Docker Hub.")]
    public string RegistryUrl { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Docker.RegistryUsername",
        Widget = StepUiWidgets.Text, Label = "Username",
        Group = "registry")]
    public string RegistryUsername { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Docker.RegistryPassword",
        Widget = StepUiWidgets.Sensitive, Label = "Password / token",
        Group = "registry")]
    public string RegistryPassword { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Docker.AdditionalArgs",
        Widget = StepUiWidgets.Text, Label = "Additional docker run arguments",
        Group = "container",
        HelpText = "Appended verbatim before the image name.")]
    public string AdditionalArgs { get; set; } = "";
}

// ── Octopus.DockerStop ───────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.dockerstop", Title = "Stop a Docker Resource",
    Version = "1.0.0",
    Description = "Stop or remove a container or network created by a previous deployment.")]
[StepUiGroup("target", "Target")]
internal sealed class OctopusDockerStopStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Docker.ResourceType",
        Widget = StepUiWidgets.Select, Label = "Resource type",
        Group = "target", Default = "container")]
    [StepUiEnum("container", "Container")]
    [StepUiEnum("network", "Network")]
    public string ResourceType { get; set; } = "container";

    [StepUiField(Key = "Octopus.Action.Docker.ResourceName",
        Widget = StepUiWidgets.Text, Label = "Resource name",
        Group = "target", Required = true,
        HelpText = "Name of the container or network to stop/remove.")]
    public string ResourceName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Docker.RemoveOnStop",
        Widget = StepUiWidgets.Checkbox, Label = "Remove after stopping",
        Group = "target", Default = "false",
        HelpText = "Also remove the container after stopping it.")]
    public bool RemoveOnStop { get; set; }

    [StepUiField(Key = "Octopus.Action.Docker.StopTimeout",
        Widget = StepUiWidgets.NumberInput, Label = "Stop timeout (seconds)",
        Group = "target", Default = "10", Min = 0)]
    public int StopTimeout { get; set; } = 10;
}

// ── Octopus.DockerNetwork ────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.dockernetwork", Title = "Create a Docker Network",
    Version = "1.0.0",
    Description = "Create a Docker network for use by containers.")]
[StepUiGroup("network", "Network")]
internal sealed class OctopusDockerNetworkStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Docker.NetworkName",
        Widget = StepUiWidgets.Text, Label = "Network name",
        Group = "network", Required = true)]
    public string NetworkName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Docker.NetworkDriver",
        Widget = StepUiWidgets.Select, Label = "Driver",
        Group = "network", Default = "bridge")]
    [StepUiEnum("bridge", "bridge")]
    [StepUiEnum("host", "host")]
    [StepUiEnum("overlay", "overlay")]
    [StepUiEnum("macvlan", "macvlan")]
    [StepUiEnum("none", "none")]
    public string NetworkDriver { get; set; } = "bridge";

    [StepUiField(Key = "Octopus.Action.Docker.Labels",
        Widget = StepUiWidgets.Textarea, Label = "Labels",
        Group = "network",
        HelpText = "One per line: key=value")]
    public string Labels { get; set; } = "";
}

// ── Kubernetes shared connection group ───────────────────────────────────
// All K8s steps share the same cluster-connection fields.

[StepUiGroup("cluster", "Cluster Connection")]
internal sealed class KubernetesConnectionSchemaFields
{
    [StepUiField(Key = "Octopus.Action.Kubernetes.ClusterUrl",
        Widget = StepUiWidgets.Text, Label = "Cluster URL",
        Group = "cluster",
        HelpText = "Kubernetes API server URL. Leave empty to use kubeconfig.")]
    public string ClusterUrl { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Token",
        Widget = StepUiWidgets.Sensitive, Label = "Service account token",
        Group = "cluster")]
    public string Token { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Namespace",
        Widget = StepUiWidgets.Text, Label = "Namespace",
        Group = "cluster", Default = "default")]
    public string Namespace { get; set; } = "default";

    [StepUiField(Key = "Octopus.Action.Kubernetes.KubeconfigPath",
        Widget = StepUiWidgets.Text, Label = "Kubeconfig path (advanced)",
        Group = "cluster",
        HelpText = "Explicit path to a kubeconfig file on the agent. Overrides ClusterUrl.")]
    public string KubeconfigPath { get; set; } = "";
}

// ── Octopus.KubernetesDeployRawYaml ──────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.kubernetesdeployrawyaml", Title = "Deploy Kubernetes YAML",
    Version = "1.0.0",
    Description = "Apply raw YAML manifests to a Kubernetes cluster.")]
[StepUiGroup("cluster", "Cluster Connection")]
[StepUiGroup("yaml", "YAML")]
internal sealed class KubernetesDeployRawYamlStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Kubernetes.ClusterUrl",
        Widget = StepUiWidgets.Text, Label = "Cluster URL", Group = "cluster")]
    public string ClusterUrl { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Token",
        Widget = StepUiWidgets.Sensitive, Label = "Token", Group = "cluster")]
    public string Token { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Namespace",
        Widget = StepUiWidgets.Text, Label = "Namespace", Group = "cluster", Default = "default")]
    public string Namespace { get; set; } = "default";

    [StepUiField(Key = "Octopus.Action.Kubernetes.KubeconfigPath",
        Widget = StepUiWidgets.Text, Label = "Kubeconfig path", Group = "cluster")]
    public string KubeconfigPath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Yaml",
        Widget = StepUiWidgets.Textarea, Label = "Inline YAML",
        Group = "yaml",
        HelpText = "YAML manifests to apply. Octostache #{...} variables are resolved.")]
    public string Yaml { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.YamlFiles",
        Widget = StepUiWidgets.Text, Label = "YAML file globs",
        Group = "yaml",
        HelpText = "Comma-separated globs relative to the package (e.g. *.yaml, k8s/*.yml).")]
    public string YamlFiles { get; set; } = "";
}

// ── Octopus.KubernetesDeployContainers ───────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.kubernetesdeploycontainers", Title = "Deploy Kubernetes Containers",
    Version = "1.0.0",
    Description = "Create or update a Kubernetes Deployment from a container image.")]
[StepUiGroup("cluster", "Cluster Connection")]
[StepUiGroup("workload", "Workload")]
internal sealed class KubernetesDeployContainersStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Kubernetes.ClusterUrl",
        Widget = StepUiWidgets.Text, Label = "Cluster URL", Group = "cluster")]
    public string ClusterUrl { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Token",
        Widget = StepUiWidgets.Sensitive, Label = "Token", Group = "cluster")]
    public string Token { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Namespace",
        Widget = StepUiWidgets.Text, Label = "Namespace", Group = "cluster", Default = "default")]
    public string Namespace { get; set; } = "default";

    [StepUiField(Key = "Octopus.Action.Kubernetes.KubeconfigPath",
        Widget = StepUiWidgets.Text, Label = "Kubeconfig path", Group = "cluster")]
    public string KubeconfigPath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.ResourceName",
        Widget = StepUiWidgets.Text, Label = "Deployment name",
        Group = "workload", Required = true)]
    public string ResourceName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Image",
        Widget = StepUiWidgets.Text, Label = "Container image",
        Group = "workload", Required = true,
        Placeholder = "nginx:latest")]
    public string Image { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Replicas",
        Widget = StepUiWidgets.NumberInput, Label = "Replicas",
        Group = "workload", Default = "1", Min = 0)]
    public int Replicas { get; set; } = 1;

    [StepUiField(Key = "Octopus.Action.Kubernetes.Ports",
        Widget = StepUiWidgets.Textarea, Label = "Container ports",
        Group = "workload", HelpText = "One per line.")]
    public string Ports { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.EnvVars",
        Widget = StepUiWidgets.Textarea, Label = "Environment variables",
        Group = "workload", HelpText = "One per line: KEY=VALUE")]
    public string EnvVars { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Labels",
        Widget = StepUiWidgets.Textarea, Label = "Labels",
        Group = "workload", HelpText = "One per line: key=value")]
    public string Labels { get; set; } = "";
}

// ── Octopus.KubernetesDeployService ──────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.kubernetesdeployservice", Title = "Deploy Kubernetes Service",
    Version = "1.0.0",
    Description = "Create or update a Kubernetes Service.")]
[StepUiGroup("cluster", "Cluster Connection")]
[StepUiGroup("service", "Service")]
internal sealed class KubernetesDeployServiceStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Kubernetes.ClusterUrl",
        Widget = StepUiWidgets.Text, Label = "Cluster URL", Group = "cluster")]
    public string ClusterUrl { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Token",
        Widget = StepUiWidgets.Sensitive, Label = "Token", Group = "cluster")]
    public string Token { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Namespace",
        Widget = StepUiWidgets.Text, Label = "Namespace", Group = "cluster", Default = "default")]
    public string Namespace { get; set; } = "default";

    [StepUiField(Key = "Octopus.Action.Kubernetes.KubeconfigPath",
        Widget = StepUiWidgets.Text, Label = "Kubeconfig path", Group = "cluster")]
    public string KubeconfigPath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.ResourceName",
        Widget = StepUiWidgets.Text, Label = "Service name",
        Group = "service", Required = true)]
    public string ResourceName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.ServiceType",
        Widget = StepUiWidgets.Select, Label = "Service type",
        Group = "service", Default = "ClusterIP")]
    [StepUiEnum("ClusterIP", "ClusterIP")]
    [StepUiEnum("NodePort", "NodePort")]
    [StepUiEnum("LoadBalancer", "LoadBalancer")]
    [StepUiEnum("ExternalName", "ExternalName")]
    public string ServiceType { get; set; } = "ClusterIP";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Ports",
        Widget = StepUiWidgets.Textarea, Label = "Ports",
        Group = "service", HelpText = "One per line: port or port:targetPort")]
    public string Ports { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Selector",
        Widget = StepUiWidgets.Textarea, Label = "Selector",
        Group = "service", HelpText = "One per line: key=value")]
    public string Selector { get; set; } = "";
}

// ── Octopus.KubernetesDeployIngress ──────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.kubernetesdeployingress", Title = "Deploy Kubernetes Ingress",
    Version = "1.0.0",
    Description = "Create or update a Kubernetes Ingress.")]
[StepUiGroup("cluster", "Cluster Connection")]
[StepUiGroup("ingress", "Ingress")]
internal sealed class KubernetesDeployIngressStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Kubernetes.ClusterUrl",
        Widget = StepUiWidgets.Text, Label = "Cluster URL", Group = "cluster")]
    public string ClusterUrl { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Token",
        Widget = StepUiWidgets.Sensitive, Label = "Token", Group = "cluster")]
    public string Token { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Namespace",
        Widget = StepUiWidgets.Text, Label = "Namespace", Group = "cluster", Default = "default")]
    public string Namespace { get; set; } = "default";

    [StepUiField(Key = "Octopus.Action.Kubernetes.KubeconfigPath",
        Widget = StepUiWidgets.Text, Label = "Kubeconfig path", Group = "cluster")]
    public string KubeconfigPath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.ResourceName",
        Widget = StepUiWidgets.Text, Label = "Ingress name",
        Group = "ingress", Required = true)]
    public string ResourceName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Rules",
        Widget = StepUiWidgets.Textarea, Label = "Rules",
        Group = "ingress",
        HelpText = "One per line: host|path|backend-service|port")]
    public string Rules { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.TlsSecretName",
        Widget = StepUiWidgets.Text, Label = "TLS secret name",
        Group = "ingress")]
    public string TlsSecretName { get; set; } = "";
}

// ── Octopus.KubernetesDeployConfigMap ────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.kubernetesdeployconfigmap", Title = "Deploy Kubernetes ConfigMap",
    Version = "1.0.0",
    Description = "Create or update a Kubernetes ConfigMap.")]
[StepUiGroup("cluster", "Cluster Connection")]
[StepUiGroup("configmap", "ConfigMap")]
internal sealed class KubernetesDeployConfigMapStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Kubernetes.ClusterUrl",
        Widget = StepUiWidgets.Text, Label = "Cluster URL", Group = "cluster")]
    public string ClusterUrl { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Token",
        Widget = StepUiWidgets.Sensitive, Label = "Token", Group = "cluster")]
    public string Token { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Namespace",
        Widget = StepUiWidgets.Text, Label = "Namespace", Group = "cluster", Default = "default")]
    public string Namespace { get; set; } = "default";

    [StepUiField(Key = "Octopus.Action.Kubernetes.KubeconfigPath",
        Widget = StepUiWidgets.Text, Label = "Kubeconfig path", Group = "cluster")]
    public string KubeconfigPath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.ResourceName",
        Widget = StepUiWidgets.Text, Label = "ConfigMap name",
        Group = "configmap", Required = true)]
    public string ResourceName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.DataEntries",
        Widget = StepUiWidgets.Textarea, Label = "Data entries",
        Group = "configmap", HelpText = "One per line: key=value")]
    public string DataEntries { get; set; } = "";
}

// ── Octopus.KubernetesDeploySecret ───────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.kubernetesdeploysecret", Title = "Deploy Kubernetes Secret",
    Version = "1.0.0",
    Description = "Create or update a Kubernetes Secret.")]
[StepUiGroup("cluster", "Cluster Connection")]
[StepUiGroup("secret", "Secret")]
internal sealed class KubernetesDeploySecretStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Kubernetes.ClusterUrl",
        Widget = StepUiWidgets.Text, Label = "Cluster URL", Group = "cluster")]
    public string ClusterUrl { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Token",
        Widget = StepUiWidgets.Sensitive, Label = "Token", Group = "cluster")]
    public string Token { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Namespace",
        Widget = StepUiWidgets.Text, Label = "Namespace", Group = "cluster", Default = "default")]
    public string Namespace { get; set; } = "default";

    [StepUiField(Key = "Octopus.Action.Kubernetes.KubeconfigPath",
        Widget = StepUiWidgets.Text, Label = "Kubeconfig path", Group = "cluster")]
    public string KubeconfigPath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.ResourceName",
        Widget = StepUiWidgets.Text, Label = "Secret name",
        Group = "secret", Required = true)]
    public string ResourceName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.SecretType",
        Widget = StepUiWidgets.Select, Label = "Secret type",
        Group = "secret", Default = "Opaque")]
    [StepUiEnum("Opaque", "Opaque")]
    [StepUiEnum("kubernetes.io/tls", "kubernetes.io/tls")]
    [StepUiEnum("kubernetes.io/dockerconfigjson", "kubernetes.io/dockerconfigjson")]
    public string SecretType { get; set; } = "Opaque";

    [StepUiField(Key = "Octopus.Action.Kubernetes.DataEntries",
        Widget = StepUiWidgets.Textarea, Label = "Data entries",
        Group = "secret", HelpText = "One per line: key=value (stored as stringData)")]
    public string DataEntries { get; set; } = "";
}

// ── Octopus.Kubernetes.Kustomize ─────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.kubernetes.kustomize", Title = "Deploy with Kustomize",
    Version = "1.0.0",
    Description = "Apply a kustomization directory to a Kubernetes cluster.")]
[StepUiGroup("cluster", "Cluster Connection")]
[StepUiGroup("kustomize", "Kustomize")]
internal sealed class KubernetesKustomizeStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Kubernetes.ClusterUrl",
        Widget = StepUiWidgets.Text, Label = "Cluster URL", Group = "cluster")]
    public string ClusterUrl { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Token",
        Widget = StepUiWidgets.Sensitive, Label = "Token", Group = "cluster")]
    public string Token { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Namespace",
        Widget = StepUiWidgets.Text, Label = "Namespace", Group = "cluster", Default = "default")]
    public string Namespace { get; set; } = "default";

    [StepUiField(Key = "Octopus.Action.Kubernetes.KubeconfigPath",
        Widget = StepUiWidgets.Text, Label = "Kubeconfig path", Group = "cluster")]
    public string KubeconfigPath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.KustomizationDir",
        Widget = StepUiWidgets.Text, Label = "Kustomization directory",
        Group = "kustomize",
        HelpText = "Path to the directory containing kustomization.yaml. Defaults to the package extract dir.")]
    public string KustomizationDir { get; set; } = "";
}

// ── Octopus.HelmChartUpgrade ─────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.helmchartupgrade", Title = "Deploy a Helm Chart",
    Version = "1.0.0",
    Description = "Install or upgrade a Helm release on a Kubernetes cluster.")]
[StepUiGroup("cluster", "Cluster Connection")]
[StepUiGroup("helm", "Helm")]
internal sealed class KubernetesHelmChartUpgradeStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Kubernetes.ClusterUrl",
        Widget = StepUiWidgets.Text, Label = "Cluster URL", Group = "cluster")]
    public string ClusterUrl { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Token",
        Widget = StepUiWidgets.Sensitive, Label = "Token", Group = "cluster")]
    public string Token { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Namespace",
        Widget = StepUiWidgets.Text, Label = "Namespace", Group = "cluster", Default = "default")]
    public string Namespace { get; set; } = "default";

    [StepUiField(Key = "Octopus.Action.Kubernetes.KubeconfigPath",
        Widget = StepUiWidgets.Text, Label = "Kubeconfig path", Group = "cluster")]
    public string KubeconfigPath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.HelmReleaseName",
        Widget = StepUiWidgets.Text, Label = "Release name",
        Group = "helm", Required = true)]
    public string HelmReleaseName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.HelmChartPath",
        Widget = StepUiWidgets.Text, Label = "Chart path or reference",
        Group = "helm", Required = true,
        HelpText = "Local path, OCI reference, or repo/chart-name.")]
    public string HelmChartPath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.HelmValues",
        Widget = StepUiWidgets.Textarea, Label = "Values (YAML)",
        Group = "helm",
        HelpText = "Inline values.yaml content. Octostache #{...} variables are resolved.")]
    public string HelmValues { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.HelmAdditionalArgs",
        Widget = StepUiWidgets.Text, Label = "Additional helm args",
        Group = "helm")]
    public string HelmAdditionalArgs { get; set; } = "";
}

// ── Octopus.KubernetesRunScript ──────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.kubernetesrunscript", Title = "Run a kubectl Script",
    Version = "1.0.0",
    Description = "Run a script with kubectl context authenticated to a Kubernetes cluster.")]
[StepUiGroup("cluster", "Cluster Connection")]
[StepUiGroup("script", "Script")]
internal sealed class KubernetesRunScriptStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Kubernetes.ClusterUrl",
        Widget = StepUiWidgets.Text, Label = "Cluster URL", Group = "cluster")]
    public string ClusterUrl { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Token",
        Widget = StepUiWidgets.Sensitive, Label = "Token", Group = "cluster")]
    public string Token { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.Namespace",
        Widget = StepUiWidgets.Text, Label = "Namespace", Group = "cluster", Default = "default")]
    public string Namespace { get; set; } = "default";

    [StepUiField(Key = "Octopus.Action.Kubernetes.KubeconfigPath",
        Widget = StepUiWidgets.Text, Label = "Kubeconfig path", Group = "cluster")]
    public string KubeconfigPath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.ScriptBody",
        Widget = StepUiWidgets.Textarea, Label = "Script body",
        Group = "script", Required = true,
        HelpText = "KUBECONFIG, KUBECTL_CONTEXT, and KUBECTL_NAMESPACE env vars are set.")]
    public string ScriptBody { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Kubernetes.ScriptSyntax",
        Widget = StepUiWidgets.Select, Label = "Language",
        Group = "script", Default = "Bash")]
    [StepUiEnum("Bash", "Bash")]
    [StepUiEnum("PowerShell", "PowerShell")]
    public string ScriptSyntax { get; set; } = "Bash";
}

// ── AWS shared credential fields ─────────────────────────────────────────

// ── Octopus.AwsUploadS3 ──────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.awsuploads3", Title = "Upload to Amazon S3",
    Version = "1.0.0",
    Description = "Upload files to an Amazon S3 bucket.")]
[StepUiGroup("credentials", "AWS Credentials")]
[StepUiGroup("s3", "S3")]
internal sealed class AwsUploadS3StepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Aws.AccessKeyId",
        Widget = StepUiWidgets.Text, Label = "Access Key ID", Group = "credentials")]
    public string AccessKeyId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.SecretAccessKey",
        Widget = StepUiWidgets.Sensitive, Label = "Secret Access Key", Group = "credentials")]
    public string SecretAccessKey { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.Region",
        Widget = StepUiWidgets.Text, Label = "Region", Group = "credentials",
        Placeholder = "eu-west-1")]
    public string Region { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.BucketName",
        Widget = StepUiWidgets.Text, Label = "Bucket name",
        Group = "s3", Required = true)]
    public string BucketName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.TargetKeyPrefix",
        Widget = StepUiWidgets.Text, Label = "Key prefix",
        Group = "s3", HelpText = "Optional prefix for uploaded object keys.")]
    public string TargetKeyPrefix { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.FileGlob",
        Widget = StepUiWidgets.Text, Label = "File glob",
        Group = "s3", Default = "**/*")]
    public string FileGlob { get; set; } = "**/*";

    [StepUiField(Key = "Octopus.Action.Aws.CannedAcl",
        Widget = StepUiWidgets.Select, Label = "Canned ACL",
        Group = "s3", Default = "private")]
    [StepUiEnum("private", "private")]
    [StepUiEnum("public-read", "public-read")]
    [StepUiEnum("public-read-write", "public-read-write")]
    [StepUiEnum("bucket-owner-full-control", "bucket-owner-full-control")]
    public string CannedAcl { get; set; } = "private";

    [StepUiField(Key = "Octopus.Action.Aws.StorageClass",
        Widget = StepUiWidgets.Select, Label = "Storage class",
        Group = "s3", Default = "STANDARD")]
    [StepUiEnum("STANDARD", "Standard")]
    [StepUiEnum("STANDARD_IA", "Standard-IA")]
    [StepUiEnum("GLACIER", "Glacier")]
    public string StorageClass { get; set; } = "STANDARD";
}

// ── Octopus.AwsCreateS3 ──────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.awscreates3", Title = "Create an S3 Bucket",
    Version = "1.0.0",
    Description = "Create a new Amazon S3 bucket.")]
[StepUiGroup("credentials", "AWS Credentials")]
[StepUiGroup("s3", "S3")]
internal sealed class AwsCreateS3StepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Aws.AccessKeyId",
        Widget = StepUiWidgets.Text, Label = "Access Key ID", Group = "credentials")]
    public string AccessKeyId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.SecretAccessKey",
        Widget = StepUiWidgets.Sensitive, Label = "Secret Access Key", Group = "credentials")]
    public string SecretAccessKey { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.Region",
        Widget = StepUiWidgets.Text, Label = "Region", Group = "credentials")]
    public string Region { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.BucketName",
        Widget = StepUiWidgets.Text, Label = "Bucket name",
        Group = "s3", Required = true)]
    public string BucketName { get; set; } = "";
}

// ── Octopus.AwsRunCloudFormation ─────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.awsruncloudformation", Title = "Deploy AWS CloudFormation",
    Version = "1.0.0",
    Description = "Create or update an AWS CloudFormation stack via change sets.")]
[StepUiGroup("credentials", "AWS Credentials")]
[StepUiGroup("stack", "Stack")]
[StepUiGroup("template", "Template")]
internal sealed class AwsRunCloudFormationStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Aws.AccessKeyId",
        Widget = StepUiWidgets.Text, Label = "Access Key ID", Group = "credentials")]
    public string AccessKeyId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.SecretAccessKey",
        Widget = StepUiWidgets.Sensitive, Label = "Secret Access Key", Group = "credentials")]
    public string SecretAccessKey { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.Region",
        Widget = StepUiWidgets.Text, Label = "Region", Group = "credentials")]
    public string Region { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.StackName",
        Widget = StepUiWidgets.Text, Label = "Stack name",
        Group = "stack", Required = true)]
    public string StackName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.WaitForCompletion",
        Widget = StepUiWidgets.Checkbox, Label = "Wait for completion",
        Group = "stack", Default = "true")]
    public bool WaitForCompletion { get; set; } = true;

    [StepUiField(Key = "Octopus.Action.Aws.Capabilities",
        Widget = StepUiWidgets.Text, Label = "Capabilities",
        Group = "stack",
        HelpText = "Space-separated (e.g. CAPABILITY_IAM CAPABILITY_NAMED_IAM).")]
    public string Capabilities { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.TemplateBody",
        Widget = StepUiWidgets.Textarea, Label = "Template body (JSON/YAML)",
        Group = "template",
        HelpText = "Inline CloudFormation template. Octostache #{...} variables are resolved.")]
    public string TemplateBody { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.TemplateFile",
        Widget = StepUiWidgets.Text, Label = "Template file path",
        Group = "template",
        HelpText = "Path to a template file. Overrides TemplateBody.")]
    public string TemplateFile { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.TemplateParameters",
        Widget = StepUiWidgets.Textarea, Label = "Parameters",
        Group = "template", HelpText = "One per line: Key=Value")]
    public string TemplateParameters { get; set; } = "";
}

// ── Octopus.AwsApplyCloudFormationChangeSet ──────────────────────────────

[StepUiSchemaRoot(Id = "octopus.awsapplycloudformationchangeset", Title = "Apply CloudFormation Change Set",
    Version = "1.0.0",
    Description = "Execute a previously created CloudFormation change set.")]
[StepUiGroup("credentials", "AWS Credentials")]
[StepUiGroup("stack", "Stack")]
internal sealed class AwsApplyChangeSetStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Aws.AccessKeyId",
        Widget = StepUiWidgets.Text, Label = "Access Key ID", Group = "credentials")]
    public string AccessKeyId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.SecretAccessKey",
        Widget = StepUiWidgets.Sensitive, Label = "Secret Access Key", Group = "credentials")]
    public string SecretAccessKey { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.Region",
        Widget = StepUiWidgets.Text, Label = "Region", Group = "credentials")]
    public string Region { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.StackName",
        Widget = StepUiWidgets.Text, Label = "Stack name",
        Group = "stack", Required = true)]
    public string StackName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.ChangeSetName",
        Widget = StepUiWidgets.Text, Label = "Change set name",
        Group = "stack", Required = true)]
    public string ChangeSetName { get; set; } = "";
}

// ── Octopus.AwsDeleteCloudFormation ──────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.awsdeletecloudformation", Title = "Delete AWS CloudFormation Stack",
    Version = "1.0.0",
    Description = "Delete an AWS CloudFormation stack and wait for completion.")]
[StepUiGroup("credentials", "AWS Credentials")]
[StepUiGroup("stack", "Stack")]
internal sealed class AwsDeleteCloudFormationStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Aws.AccessKeyId",
        Widget = StepUiWidgets.Text, Label = "Access Key ID", Group = "credentials")]
    public string AccessKeyId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.SecretAccessKey",
        Widget = StepUiWidgets.Sensitive, Label = "Secret Access Key", Group = "credentials")]
    public string SecretAccessKey { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.Region",
        Widget = StepUiWidgets.Text, Label = "Region", Group = "credentials")]
    public string Region { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.StackName",
        Widget = StepUiWidgets.Text, Label = "Stack name",
        Group = "stack", Required = true)]
    public string StackName { get; set; } = "";
}

// ── aws-ecs / aws-ecs-update-service ─────────────────────────────────────

[StepUiSchemaRoot(Id = "aws-ecs", Title = "Deploy Amazon ECS Service",
    Version = "1.0.0",
    Description = "Update an Amazon ECS service with a new task definition or desired count.")]
[StepUiGroup("credentials", "AWS Credentials")]
[StepUiGroup("ecs", "ECS")]
internal sealed class AwsEcsDeployStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Aws.AccessKeyId",
        Widget = StepUiWidgets.Text, Label = "Access Key ID", Group = "credentials")]
    public string AccessKeyId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.SecretAccessKey",
        Widget = StepUiWidgets.Sensitive, Label = "Secret Access Key", Group = "credentials")]
    public string SecretAccessKey { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.Region",
        Widget = StepUiWidgets.Text, Label = "Region", Group = "credentials")]
    public string Region { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.ClusterName",
        Widget = StepUiWidgets.Text, Label = "Cluster name",
        Group = "ecs", Required = true)]
    public string ClusterName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.ServiceName",
        Widget = StepUiWidgets.Text, Label = "Service name",
        Group = "ecs", Required = true)]
    public string ServiceName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.TaskDefinition",
        Widget = StepUiWidgets.Text, Label = "Task definition",
        Group = "ecs",
        HelpText = "Family:revision or full ARN. Leave empty to keep current.")]
    public string TaskDefinition { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.DesiredCount",
        Widget = StepUiWidgets.NumberInput, Label = "Desired count",
        Group = "ecs", Min = 0)]
    public int DesiredCount { get; set; }

    [StepUiField(Key = "Octopus.Action.Aws.ForceNewDeployment",
        Widget = StepUiWidgets.Checkbox, Label = "Force new deployment",
        Group = "ecs", Default = "false")]
    public bool ForceNewDeployment { get; set; }
}

// ── Octopus.AwsRunScript ─────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.awsrunscript", Title = "Run an AWS Script",
    Version = "1.0.0",
    Description = "Run a script with AWS credentials set as environment variables.")]
[StepUiGroup("credentials", "AWS Credentials")]
[StepUiGroup("script", "Script")]
internal sealed class AwsRunScriptStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Aws.AccessKeyId",
        Widget = StepUiWidgets.Text, Label = "Access Key ID", Group = "credentials")]
    public string AccessKeyId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.SecretAccessKey",
        Widget = StepUiWidgets.Sensitive, Label = "Secret Access Key", Group = "credentials")]
    public string SecretAccessKey { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.Region",
        Widget = StepUiWidgets.Text, Label = "Region", Group = "credentials")]
    public string Region { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.ScriptBody",
        Widget = StepUiWidgets.Textarea, Label = "Script body",
        Group = "script", Required = true,
        HelpText = "AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, and AWS_DEFAULT_REGION env vars are set.")]
    public string ScriptBody { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Aws.ScriptSyntax",
        Widget = StepUiWidgets.Select, Label = "Language",
        Group = "script", Default = "Bash")]
    [StepUiEnum("Bash", "Bash")]
    [StepUiEnum("PowerShell", "PowerShell")]
    public string ScriptSyntax { get; set; } = "Bash";
}

// ── Octopus.AzureWebApp / Octopus.AzureAppService ────────────────────────

[StepUiSchemaRoot(Id = "octopus.azurewebapp", Title = "Deploy to Azure Web App",
    Version = "1.0.0",
    Description = "Deploy a package to an Azure App Service (Web App).")]
[StepUiGroup("credentials", "Azure Credentials")]
[StepUiGroup("webapp", "Web App")]
internal sealed class AzureWebAppStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Azure.ServicePrincipalAppId",
        Widget = StepUiWidgets.Text, Label = "Service Principal App ID", Group = "credentials")]
    public string ServicePrincipalAppId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.ServicePrincipalPassword",
        Widget = StepUiWidgets.Sensitive, Label = "Service Principal Password", Group = "credentials")]
    public string ServicePrincipalPassword { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.TenantId",
        Widget = StepUiWidgets.Text, Label = "Tenant ID", Group = "credentials")]
    public string TenantId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.SubscriptionId",
        Widget = StepUiWidgets.Text, Label = "Subscription ID", Group = "credentials")]
    public string SubscriptionId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.ResourceGroupName",
        Widget = StepUiWidgets.Text, Label = "Resource group",
        Group = "webapp", Required = true)]
    public string ResourceGroupName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.WebAppName",
        Widget = StepUiWidgets.Text, Label = "Web App name",
        Group = "webapp", Required = true)]
    public string WebAppName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.WebAppSlot",
        Widget = StepUiWidgets.Text, Label = "Slot",
        Group = "webapp",
        HelpText = "Leave empty for production slot.")]
    public string WebAppSlot { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.PackageUri",
        Widget = StepUiWidgets.Text, Label = "Package URI (optional)",
        Group = "webapp",
        HelpText = "External zip URL. Leave empty to use the deployment package.")]
    public string PackageUri { get; set; } = "";
}

// ── Octopus.AzurePowerShell ──────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.azurepowershell", Title = "Run an Azure PowerShell Script",
    Version = "1.0.0",
    Description = "Run a PowerShell script with Azure credentials set as environment variables.")]
[StepUiGroup("credentials", "Azure Credentials")]
[StepUiGroup("script", "Script")]
internal sealed class AzurePowerShellStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Azure.ServicePrincipalAppId",
        Widget = StepUiWidgets.Text, Label = "Service Principal App ID", Group = "credentials")]
    public string ServicePrincipalAppId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.ServicePrincipalPassword",
        Widget = StepUiWidgets.Sensitive, Label = "Service Principal Password", Group = "credentials")]
    public string ServicePrincipalPassword { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.TenantId",
        Widget = StepUiWidgets.Text, Label = "Tenant ID", Group = "credentials")]
    public string TenantId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.SubscriptionId",
        Widget = StepUiWidgets.Text, Label = "Subscription ID", Group = "credentials")]
    public string SubscriptionId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.ScriptBody",
        Widget = StepUiWidgets.Textarea, Label = "Script body",
        Group = "script", Required = true,
        HelpText = "AZURE_CLIENT_ID, AZURE_CLIENT_SECRET, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID env vars are set.")]
    public string ScriptBody { get; set; } = "";
}

// ── Octopus.AzureResourceGroup ───────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.azureresourcegroup", Title = "Deploy an Azure ARM Template",
    Version = "1.0.0",
    Description = "Deploy an Azure Resource Manager (ARM) template to a resource group.")]
[StepUiGroup("credentials", "Azure Credentials")]
[StepUiGroup("deployment", "Deployment")]
[StepUiGroup("template", "Template")]
internal sealed class AzureResourceGroupStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Azure.ServicePrincipalAppId",
        Widget = StepUiWidgets.Text, Label = "Service Principal App ID", Group = "credentials")]
    public string ServicePrincipalAppId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.ServicePrincipalPassword",
        Widget = StepUiWidgets.Sensitive, Label = "Service Principal Password", Group = "credentials")]
    public string ServicePrincipalPassword { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.TenantId",
        Widget = StepUiWidgets.Text, Label = "Tenant ID", Group = "credentials")]
    public string TenantId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.SubscriptionId",
        Widget = StepUiWidgets.Text, Label = "Subscription ID", Group = "credentials")]
    public string SubscriptionId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.ResourceGroupName",
        Widget = StepUiWidgets.Text, Label = "Resource group",
        Group = "deployment", Required = true)]
    public string ResourceGroupName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.DeploymentName",
        Widget = StepUiWidgets.Text, Label = "Deployment name",
        Group = "deployment",
        HelpText = "Auto-generated if left empty.")]
    public string DeploymentName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.DeploymentMode",
        Widget = StepUiWidgets.Select, Label = "Deployment mode",
        Group = "deployment", Default = "Incremental")]
    [StepUiEnum("Incremental", "Incremental")]
    [StepUiEnum("Complete", "Complete")]
    [StepUiEnum("Validate", "Validate")]
    public string DeploymentMode { get; set; } = "Incremental";

    [StepUiField(Key = "Octopus.Action.Azure.TemplateFile",
        Widget = StepUiWidgets.Text, Label = "Template file path",
        Group = "template",
        HelpText = "Path to an ARM template JSON file.")]
    public string TemplateFile { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.TemplateBody",
        Widget = StepUiWidgets.Textarea, Label = "Template body (JSON)",
        Group = "template",
        HelpText = "Inline ARM template. Octostache #{...} variables are resolved.")]
    public string TemplateBody { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.TemplateParameters",
        Widget = StepUiWidgets.Textarea, Label = "Parameters",
        Group = "template", HelpText = "One per line: Key=Value")]
    public string TemplateParameters { get; set; } = "";
}

// ── deploy-a-bicep-template ──────────────────────────────────────────────

[StepUiSchemaRoot(Id = "deploy-a-bicep-template", Title = "Deploy a Bicep Template",
    Version = "1.0.0",
    Description = "Deploy an Azure Bicep template to a resource group.")]
[StepUiGroup("credentials", "Azure Credentials")]
[StepUiGroup("deployment", "Deployment")]
[StepUiGroup("bicep", "Bicep")]
internal sealed class AzureBicepStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Azure.ServicePrincipalAppId",
        Widget = StepUiWidgets.Text, Label = "Service Principal App ID", Group = "credentials")]
    public string ServicePrincipalAppId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.ServicePrincipalPassword",
        Widget = StepUiWidgets.Sensitive, Label = "Service Principal Password", Group = "credentials")]
    public string ServicePrincipalPassword { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.TenantId",
        Widget = StepUiWidgets.Text, Label = "Tenant ID", Group = "credentials")]
    public string TenantId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.SubscriptionId",
        Widget = StepUiWidgets.Text, Label = "Subscription ID", Group = "credentials")]
    public string SubscriptionId { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.ResourceGroupName",
        Widget = StepUiWidgets.Text, Label = "Resource group",
        Group = "deployment", Required = true)]
    public string ResourceGroupName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.DeploymentName",
        Widget = StepUiWidgets.Text, Label = "Deployment name",
        Group = "deployment")]
    public string DeploymentName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.DeploymentMode",
        Widget = StepUiWidgets.Select, Label = "Deployment mode",
        Group = "deployment", Default = "Incremental")]
    [StepUiEnum("Incremental", "Incremental")]
    [StepUiEnum("Complete", "Complete")]
    [StepUiEnum("Validate", "Validate")]
    public string DeploymentMode { get; set; } = "Incremental";

    [StepUiField(Key = "Octopus.Action.Azure.BicepFile",
        Widget = StepUiWidgets.Text, Label = "Bicep file path",
        Group = "bicep",
        HelpText = "Path to a .bicep file. Auto-detected from package if empty.")]
    public string BicepFile { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Azure.TemplateParameters",
        Widget = StepUiWidgets.Textarea, Label = "Parameters",
        Group = "bicep", HelpText = "One per line: Key=Value")]
    public string TemplateParameters { get; set; } = "";
}

// ── Octopus.JavaArchive ──────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.javaarchive", Title = "Deploy a Java Archive",
    Version = "1.0.0",
    Description = "Deploy a WAR, JAR, or EAR file to a target directory.")]
[StepUiGroup("general", "General")]
internal sealed class JavaArchiveStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Java.DeployPath",
        Widget = StepUiWidgets.Text, Label = "Deployment path",
        Group = "general", Required = true,
        Placeholder = "/opt/apps")]
    public string DeployPath { get; set; } = "";
}

// ── Octopus.TomcatDeploy ─────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.tomcatdeploy", Title = "Deploy to Tomcat",
    Version = "1.0.0",
    Description = "Deploy a WAR file to an Apache Tomcat instance.")]
[StepUiGroup("tomcat", "Tomcat")]
internal sealed class TomcatDeployStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Java.TomcatHome",
        Widget = StepUiWidgets.Text, Label = "Tomcat home",
        Group = "tomcat",
        HelpText = "Path to the Tomcat installation (CATALINA_HOME).")]
    public string TomcatHome { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.DeployPath",
        Widget = StepUiWidgets.Text, Label = "Webapps directory",
        Group = "tomcat",
        HelpText = "Defaults to {TomcatHome}/webapps when empty.")]
    public string DeployPath { get; set; } = "";
}

// ── Octopus.TomcatState ──────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.tomcatstate", Title = "Manage Tomcat State",
    Version = "1.0.0",
    Description = "Start, stop, or restart an Apache Tomcat instance.")]
[StepUiGroup("tomcat", "Tomcat")]
internal sealed class TomcatStateStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Java.TomcatAction",
        Widget = StepUiWidgets.Select, Label = "Action",
        Group = "tomcat", Default = "restart")]
    [StepUiEnum("start", "Start")]
    [StepUiEnum("stop", "Stop")]
    [StepUiEnum("restart", "Restart")]
    public string TomcatAction { get; set; } = "restart";

    [StepUiField(Key = "Octopus.Action.Java.TomcatHome",
        Widget = StepUiWidgets.Text, Label = "Tomcat home",
        Group = "tomcat",
        HelpText = "Path to the Tomcat installation. Uses catalina.sh/bat.")]
    public string TomcatHome { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.TomcatServiceName",
        Widget = StepUiWidgets.Text, Label = "Service name",
        Group = "tomcat",
        HelpText = "OS service name. Overrides TomcatHome when set.")]
    public string TomcatServiceName { get; set; } = "";
}

// ── Octopus.TomcatDeployCertificate ──────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.tomcatdeploycertificate", Title = "Deploy Certificate to Tomcat",
    Version = "1.0.0",
    Description = "Import a certificate into a Tomcat Java keystore.")]
[StepUiGroup("keystore", "Keystore")]
internal sealed class TomcatCertificateStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Java.TomcatKeystorePath",
        Widget = StepUiWidgets.Text, Label = "Keystore path",
        Group = "keystore", Required = true)]
    public string TomcatKeystorePath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.TomcatKeystorePassword",
        Widget = StepUiWidgets.Sensitive, Label = "Keystore password",
        Group = "keystore")]
    public string TomcatKeystorePassword { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.CertificatePath",
        Widget = StepUiWidgets.Text, Label = "Certificate file path",
        Group = "keystore",
        HelpText = "Path to a .pfx/.p12/.jks file. Auto-detected from package if empty.")]
    public string CertificatePath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.CertificatePassword",
        Widget = StepUiWidgets.Sensitive, Label = "Certificate password",
        Group = "keystore")]
    public string CertificatePassword { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.KeystoreAlias",
        Widget = StepUiWidgets.Text, Label = "Alias",
        Group = "keystore", Default = "tomcat")]
    public string KeystoreAlias { get; set; } = "tomcat";

    [StepUiField(Key = "Octopus.Action.Java.JavaHome",
        Widget = StepUiWidgets.Text, Label = "JAVA_HOME",
        Group = "keystore",
        HelpText = "Path to JDK. Defaults to keytool on PATH.")]
    public string JavaHome { get; set; } = "";
}

// ── Octopus.WildFlyDeploy ────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.wildflydeploy", Title = "Deploy to WildFly",
    Version = "1.0.0",
    Description = "Deploy a WAR or EAR to a WildFly/JBoss instance via CLI.")]
[StepUiGroup("server", "Server")]
[StepUiGroup("deployment", "Deployment")]
internal sealed class WildFlyDeployStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Java.WildFlyHome",
        Widget = StepUiWidgets.Text, Label = "WildFly home",
        Group = "server",
        HelpText = "Path to the WildFly installation (JBOSS_HOME).")]
    public string WildFlyHome { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.WildFlyHost",
        Widget = StepUiWidgets.Text, Label = "Host",
        Group = "server", Default = "localhost")]
    public string WildFlyHost { get; set; } = "localhost";

    [StepUiField(Key = "Octopus.Action.Java.WildFlyPort",
        Widget = StepUiWidgets.Text, Label = "Management port",
        Group = "server", Default = "9990")]
    public string WildFlyPort { get; set; } = "9990";

    [StepUiField(Key = "Octopus.Action.Java.WildFlyUser",
        Widget = StepUiWidgets.Text, Label = "Management user",
        Group = "server")]
    public string WildFlyUser { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.WildFlyPassword",
        Widget = StepUiWidgets.Sensitive, Label = "Management password",
        Group = "server")]
    public string WildFlyPassword { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.DeploymentName",
        Widget = StepUiWidgets.Text, Label = "Deployment name",
        Group = "deployment",
        HelpText = "Defaults to the archive file name.")]
    public string DeploymentName { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.ForceDeploy",
        Widget = StepUiWidgets.Checkbox, Label = "Force (replace existing)",
        Group = "deployment", Default = "false")]
    public bool ForceDeploy { get; set; }

    [StepUiField(Key = "Octopus.Action.Java.WildFlyServerGroupName",
        Widget = StepUiWidgets.Text, Label = "Server group (domain mode)",
        Group = "deployment")]
    public string WildFlyServerGroupName { get; set; } = "";
}

// ── Octopus.WildFlyState ─────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.wildflystate", Title = "Manage WildFly State",
    Version = "1.0.0",
    Description = "Start, stop, restart, or reload a WildFly/JBoss instance.")]
[StepUiGroup("server", "Server")]
internal sealed class WildFlyStateStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Java.WildFlyAction",
        Widget = StepUiWidgets.Select, Label = "Action",
        Group = "server", Default = "restart")]
    [StepUiEnum("start", "Start")]
    [StepUiEnum("stop", "Stop")]
    [StepUiEnum("restart", "Restart")]
    [StepUiEnum("reload", "Reload")]
    public string WildFlyAction { get; set; } = "restart";

    [StepUiField(Key = "Octopus.Action.Java.WildFlyHome",
        Widget = StepUiWidgets.Text, Label = "WildFly home",
        Group = "server")]
    public string WildFlyHome { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.WildFlyHost",
        Widget = StepUiWidgets.Text, Label = "Host",
        Group = "server", Default = "localhost")]
    public string WildFlyHost { get; set; } = "localhost";

    [StepUiField(Key = "Octopus.Action.Java.WildFlyPort",
        Widget = StepUiWidgets.Text, Label = "Management port",
        Group = "server", Default = "9990")]
    public string WildFlyPort { get; set; } = "9990";

    [StepUiField(Key = "Octopus.Action.Java.WildFlyUser",
        Widget = StepUiWidgets.Text, Label = "Management user",
        Group = "server")]
    public string WildFlyUser { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.WildFlyPassword",
        Widget = StepUiWidgets.Sensitive, Label = "Management password",
        Group = "server")]
    public string WildFlyPassword { get; set; } = "";
}

// ── Octopus.WildFlyCertificateDeploy ─────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.wildflycertificatedeploy", Title = "Deploy Certificate to WildFly",
    Version = "1.0.0",
    Description = "Import a certificate into a WildFly keystore.")]
[StepUiGroup("keystore", "Keystore")]
internal sealed class WildFlyCertificateStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Java.KeystorePath",
        Widget = StepUiWidgets.Text, Label = "Keystore path",
        Group = "keystore", Required = true)]
    public string KeystorePath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.KeystorePassword",
        Widget = StepUiWidgets.Sensitive, Label = "Keystore password",
        Group = "keystore")]
    public string KeystorePassword { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.CertificatePath",
        Widget = StepUiWidgets.Text, Label = "Certificate file path",
        Group = "keystore",
        HelpText = "Path to a .pfx/.p12 file. Auto-detected from package if empty.")]
    public string CertificatePath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.CertificatePassword",
        Widget = StepUiWidgets.Sensitive, Label = "Certificate password",
        Group = "keystore")]
    public string CertificatePassword { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.KeystoreAlias",
        Widget = StepUiWidgets.Text, Label = "Alias",
        Group = "keystore", Default = "wildfly")]
    public string KeystoreAlias { get; set; } = "wildfly";

    [StepUiField(Key = "Octopus.Action.Java.JavaHome",
        Widget = StepUiWidgets.Text, Label = "JAVA_HOME",
        Group = "keystore")]
    public string JavaHome { get; set; } = "";
}

// ── Octopus.JavaDeployCertificate ────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.javadeploycertificate", Title = "Deploy a Java Certificate",
    Version = "1.0.0",
    Description = "Import a certificate into a generic Java keystore via keytool.")]
[StepUiGroup("keystore", "Keystore")]
internal sealed class JavaDeployCertificateStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Java.KeystorePath",
        Widget = StepUiWidgets.Text, Label = "Keystore path",
        Group = "keystore", Required = true)]
    public string KeystorePath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.KeystorePassword",
        Widget = StepUiWidgets.Sensitive, Label = "Keystore password",
        Group = "keystore")]
    public string KeystorePassword { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.KeystoreType",
        Widget = StepUiWidgets.Select, Label = "Keystore type",
        Group = "keystore", Default = "PKCS12")]
    [StepUiEnum("PKCS12", "PKCS12")]
    [StepUiEnum("JKS", "JKS")]
    public string KeystoreType { get; set; } = "PKCS12";

    [StepUiField(Key = "Octopus.Action.Java.CertificatePath",
        Widget = StepUiWidgets.Text, Label = "Certificate file path",
        Group = "keystore",
        HelpText = "Path to a .cer/.crt/.pem file. Auto-detected from package if empty.")]
    public string CertificatePath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Java.KeystoreAlias",
        Widget = StepUiWidgets.Text, Label = "Alias",
        Group = "keystore", Default = "kraken")]
    public string KeystoreAlias { get; set; } = "kraken";

    [StepUiField(Key = "Octopus.Action.Java.JavaHome",
        Widget = StepUiWidgets.Text, Label = "JAVA_HOME",
        Group = "keystore")]
    public string JavaHome { get; set; } = "";
}

// ── Octopus.TerraformApply ───────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.terraformapply", Title = "Apply a Terraform Template",
    Version = "1.0.0",
    Description = "Run terraform init and apply to provision infrastructure.")]
[StepUiGroup("general", "General")]
[StepUiGroup("variables", "Variables", Collapsed = true)]
[StepUiGroup("advanced", "Advanced", Collapsed = true)]
internal sealed class TerraformApplyStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Terraform.WorkingDirectory",
        Widget = StepUiWidgets.Text, Label = "Working directory",
        Group = "general",
        HelpText = "Directory containing .tf files. Defaults to the package extract dir.")]
    public string WorkingDirectory { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.Workspace",
        Widget = StepUiWidgets.Text, Label = "Workspace",
        Group = "general",
        HelpText = "Terraform workspace to select (created if it doesn't exist).")]
    public string Workspace { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.AutoApprove",
        Widget = StepUiWidgets.Checkbox, Label = "Auto-approve",
        Group = "general", Default = "true")]
    public bool AutoApprove { get; set; } = true;

    [StepUiField(Key = "Octopus.Action.Terraform.Vars",
        Widget = StepUiWidgets.Textarea, Label = "Variables",
        Group = "variables",
        HelpText = "One per line: key=value. Octostache #{...} placeholders are resolved. TF_VAR_* deployment variables are also passed as env vars.")]
    public string Vars { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.VarFile",
        Widget = StepUiWidgets.Textarea, Label = "Var files",
        Group = "variables",
        HelpText = "One per line: path to a .tfvars file.")]
    public string VarFile { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.BackendConfig",
        Widget = StepUiWidgets.Textarea, Label = "Backend config",
        Group = "advanced",
        HelpText = "One per line: key=value pairs passed to terraform init -backend-config.")]
    public string BackendConfig { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.AdditionalInitArgs",
        Widget = StepUiWidgets.Text, Label = "Additional init args",
        Group = "advanced")]
    public string AdditionalInitArgs { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.AdditionalActionArgs",
        Widget = StepUiWidgets.Text, Label = "Additional plan args",
        Group = "advanced")]
    public string AdditionalActionArgs { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.SkipInit",
        Widget = StepUiWidgets.Checkbox, Label = "Skip terraform init",
        Group = "advanced", Default = "false")]
    public bool SkipInit { get; set; }
}

// ── Octopus.Email ────────────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.email", Title = "Send an Email",
    Version = "1.0.0",
    Description = "Send an email notification via SMTP.")]
[StepUiGroup("smtp", "SMTP Server")]
[StepUiGroup("message", "Message")]
internal sealed class EmailStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Email.SmtpHost",
        Widget = StepUiWidgets.Text, Label = "SMTP host",
        Group = "smtp", Required = true)]
    public string SmtpHost { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Email.SmtpPort",
        Widget = StepUiWidgets.NumberInput, Label = "SMTP port",
        Group = "smtp", Default = "25")]
    public int SmtpPort { get; set; } = 25;

    [StepUiField(Key = "Octopus.Action.Email.SmtpUsername",
        Widget = StepUiWidgets.Text, Label = "Username",
        Group = "smtp")]
    public string SmtpUsername { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Email.SmtpPassword",
        Widget = StepUiWidgets.Sensitive, Label = "Password",
        Group = "smtp")]
    public string SmtpPassword { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Email.SmtpUseSsl",
        Widget = StepUiWidgets.Checkbox, Label = "Use SSL/TLS",
        Group = "smtp", Default = "false")]
    public bool SmtpUseSsl { get; set; }

    [StepUiField(Key = "Octopus.Action.Email.From",
        Widget = StepUiWidgets.Text, Label = "From",
        Group = "message", Default = "kraken@localhost")]
    public string From { get; set; } = "kraken@localhost";

    [StepUiField(Key = "Octopus.Action.Email.To",
        Widget = StepUiWidgets.Text, Label = "To",
        Group = "message", Required = true,
        HelpText = "Comma- or semicolon-separated addresses.")]
    public string To { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Email.Cc",
        Widget = StepUiWidgets.Text, Label = "CC",
        Group = "message")]
    public string Cc { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Email.Bcc",
        Widget = StepUiWidgets.Text, Label = "BCC",
        Group = "message")]
    public string Bcc { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Email.Subject",
        Widget = StepUiWidgets.Text, Label = "Subject",
        Group = "message",
        HelpText = "Octostache #{...} placeholders are resolved.")]
    public string Subject { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Email.Body",
        Widget = StepUiWidgets.Textarea, Label = "Body",
        Group = "message",
        HelpText = "Octostache #{...} placeholders are resolved.")]
    public string Body { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Email.IsHtml",
        Widget = StepUiWidgets.Checkbox, Label = "HTML body",
        Group = "message", Default = "false")]
    public bool IsHtml { get; set; }
}

// ── Octopus.Nginx ────────────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.nginx", Title = "Manage Nginx",
    Version = "1.0.0",
    Description = "Write an nginx config and reload/restart the service.")]
[StepUiGroup("config", "Configuration")]
[StepUiGroup("service", "Service")]
internal sealed class NginxStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Nginx.Action",
        Widget = StepUiWidgets.Select, Label = "Action",
        Group = "service", Default = "reload")]
    [StepUiEnum("reload", "Reload")]
    [StepUiEnum("restart", "Restart")]
    [StepUiEnum("start", "Start")]
    [StepUiEnum("stop", "Stop")]
    public string Action { get; set; } = "reload";

    [StepUiField(Key = "Octopus.Action.Nginx.ServiceName",
        Widget = StepUiWidgets.Text, Label = "Service name",
        Group = "service", Default = "nginx")]
    public string ServiceName { get; set; } = "nginx";

    [StepUiField(Key = "Octopus.Action.Nginx.TestConfig",
        Widget = StepUiWidgets.Checkbox, Label = "Test config before reload (nginx -t)",
        Group = "service", Default = "true")]
    public bool TestConfig { get; set; } = true;

    [StepUiField(Key = "Octopus.Action.Nginx.ConfigPath",
        Widget = StepUiWidgets.Text, Label = "Config file path",
        Group = "config",
        HelpText = "Where to write the config body (e.g. /etc/nginx/conf.d/app.conf).")]
    public string ConfigPath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Nginx.ConfigBody",
        Widget = StepUiWidgets.Textarea, Label = "Config body",
        Group = "config",
        HelpText = "Nginx config content. Octostache #{...} placeholders are resolved.")]
    public string ConfigBody { get; set; } = "";
}

// ── Octopus.Certificate.Import ───────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.certificate.import", Title = "Import a Certificate",
    Version = "1.0.0",
    Description = "Import a certificate into the Windows certificate store.")]
[StepUiGroup("certificate", "Certificate")]
[StepUiGroup("store", "Store")]
internal sealed class CertificateImportStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Certificate.FilePath",
        Widget = StepUiWidgets.Text, Label = "Certificate file path",
        Group = "certificate",
        HelpText = "Path to a .pfx/.p12/.cer file. Auto-detected from package if empty.")]
    public string FilePath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Certificate.Password",
        Widget = StepUiWidgets.Sensitive, Label = "Certificate password",
        Group = "certificate")]
    public string Password { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Certificate.StoreName",
        Widget = StepUiWidgets.Select, Label = "Store name",
        Group = "store", Default = "My")]
    [StepUiEnum("My", "Personal (My)")]
    [StepUiEnum("Root", "Trusted Root (Root)")]
    [StepUiEnum("CA", "Intermediate CA (CA)")]
    [StepUiEnum("TrustedPublisher", "Trusted Publishers")]
    public string StoreName { get; set; } = "My";

    [StepUiField(Key = "Octopus.Action.Certificate.StoreLocation",
        Widget = StepUiWidgets.Select, Label = "Store location",
        Group = "store", Default = "LocalMachine")]
    [StepUiEnum("LocalMachine", "Local Machine")]
    [StepUiEnum("CurrentUser", "Current User")]
    public string StoreLocation { get; set; } = "LocalMachine";
}

// ── Octopus.Vhd ──────────────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.vhd", Title = "Transfer a VHD",
    Version = "1.0.0",
    Description = "Copy or expand a VHD/VHDX disk image.")]
[StepUiGroup("general", "General")]
internal sealed class VhdStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Vhd.Action",
        Widget = StepUiWidgets.Select, Label = "Action",
        Group = "general", Default = "copy")]
    [StepUiEnum("copy", "Copy")]
    [StepUiEnum("expand", "Expand (Windows only)")]
    public string Action { get; set; } = "copy";

    [StepUiField(Key = "Octopus.Action.Vhd.SourcePath",
        Widget = StepUiWidgets.Text, Label = "Source path",
        Group = "general",
        HelpText = "Path to the .vhd/.vhdx file. Auto-detected from package if empty.")]
    public string SourcePath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Vhd.DestinationPath",
        Widget = StepUiWidgets.Text, Label = "Destination path",
        Group = "general", Required = true)]
    public string DestinationPath { get; set; } = "";
}

// ── Octopus.TerraformPlan ────────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.terraformplan", Title = "Plan a Terraform Deployment",
    Version = "1.0.0",
    Description = "Run terraform init and plan to preview infrastructure changes.")]
[StepUiGroup("general", "General")]
[StepUiGroup("variables", "Variables", Collapsed = true)]
[StepUiGroup("advanced", "Advanced", Collapsed = true)]
internal sealed class TerraformPlanStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Terraform.WorkingDirectory",
        Widget = StepUiWidgets.Text, Label = "Working directory",
        Group = "general")]
    public string WorkingDirectory { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.Workspace",
        Widget = StepUiWidgets.Text, Label = "Workspace",
        Group = "general")]
    public string Workspace { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.PlanFilePath",
        Widget = StepUiWidgets.Text, Label = "Plan output file",
        Group = "general",
        HelpText = "Optional path to save the plan file (-out).")]
    public string PlanFilePath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.Vars",
        Widget = StepUiWidgets.Textarea, Label = "Variables",
        Group = "variables",
        HelpText = "One per line: key=value.")]
    public string Vars { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.VarFile",
        Widget = StepUiWidgets.Textarea, Label = "Var files",
        Group = "variables")]
    public string VarFile { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.BackendConfig",
        Widget = StepUiWidgets.Textarea, Label = "Backend config",
        Group = "advanced")]
    public string BackendConfig { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.AdditionalInitArgs",
        Widget = StepUiWidgets.Text, Label = "Additional init args",
        Group = "advanced")]
    public string AdditionalInitArgs { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.AdditionalActionArgs",
        Widget = StepUiWidgets.Text, Label = "Additional plan args",
        Group = "advanced")]
    public string AdditionalActionArgs { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.SkipInit",
        Widget = StepUiWidgets.Checkbox, Label = "Skip terraform init",
        Group = "advanced", Default = "false")]
    public bool SkipInit { get; set; }
}

// ── Octopus.TerraformDestroy ─────────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.terraformdestroy", Title = "Destroy Terraform Resources",
    Version = "1.0.0",
    Description = "Run terraform init and destroy to tear down infrastructure.")]
[StepUiGroup("general", "General")]
[StepUiGroup("variables", "Variables", Collapsed = true)]
[StepUiGroup("advanced", "Advanced", Collapsed = true)]
internal sealed class TerraformDestroyStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Terraform.WorkingDirectory",
        Widget = StepUiWidgets.Text, Label = "Working directory",
        Group = "general")]
    public string WorkingDirectory { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.Workspace",
        Widget = StepUiWidgets.Text, Label = "Workspace",
        Group = "general")]
    public string Workspace { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.Vars",
        Widget = StepUiWidgets.Textarea, Label = "Variables",
        Group = "variables",
        HelpText = "One per line: key=value.")]
    public string Vars { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.VarFile",
        Widget = StepUiWidgets.Textarea, Label = "Var files",
        Group = "variables")]
    public string VarFile { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.BackendConfig",
        Widget = StepUiWidgets.Textarea, Label = "Backend config",
        Group = "advanced")]
    public string BackendConfig { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.AdditionalInitArgs",
        Widget = StepUiWidgets.Text, Label = "Additional init args",
        Group = "advanced")]
    public string AdditionalInitArgs { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.AdditionalActionArgs",
        Widget = StepUiWidgets.Text, Label = "Additional plan args",
        Group = "advanced")]
    public string AdditionalActionArgs { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.SkipInit",
        Widget = StepUiWidgets.Checkbox, Label = "Skip terraform init",
        Group = "advanced", Default = "false")]
    public bool SkipInit { get; set; }
}

// ── Kraken.RunPackageExecutable ──────────────────────────────────────────

[StepUiSchemaRoot(Id = "kraken.runpackageexecutable", Title = "Run a Package Executable",
    Version = "1.0.0",
    Description = "Run an executable or binary shipped inside the deployment package.")]
[StepUiGroup("general", "General")]
[StepUiGroup("advanced", "Advanced", Collapsed = true)]
internal sealed class RunPackageExecutableStepSchemaShape
{
    [StepUiField(Key = "Kraken.Action.PackageRunner.ExecutablePath",
        Widget = StepUiWidgets.Text, Label = "Executable path",
        Group = "general", Required = true,
        HelpText = "Path relative to the package root (e.g. tools/migrate.exe) or absolute.",
        Placeholder = "tools/migrate.exe")]
    public string ExecutablePath { get; set; } = "";

    [StepUiField(Key = "Kraken.Action.PackageRunner.Arguments",
        Widget = StepUiWidgets.Text, Label = "Arguments",
        Group = "general",
        HelpText = "Command-line arguments. Octostache #{...} variables are resolved.")]
    public string Arguments { get; set; } = "";

    [StepUiField(Key = "Kraken.Action.PackageRunner.WorkingDirectory",
        Widget = StepUiWidgets.Text, Label = "Working directory",
        Group = "advanced",
        HelpText = "Defaults to the package extract directory.")]
    public string WorkingDirectory { get; set; } = "";

    [StepUiField(Key = "Kraken.Action.PackageRunner.TimeoutSeconds",
        Widget = StepUiWidgets.NumberInput, Label = "Timeout (seconds)",
        Group = "advanced", Default = "600", Min = 1)]
    public int TimeoutSeconds { get; set; } = 600;
}

// ── Kraken.RunPackageAssembly ────────────────────────────────────────────

[StepUiSchemaRoot(Id = "kraken.runpackageassembly", Title = "Run a .NET Assembly Step",
    Version = "1.0.0",
    Description = "Load a .NET DLL from the package and invoke its IKrakenStep implementation.")]
[StepUiGroup("general", "General")]
[StepUiGroup("advanced", "Advanced", Collapsed = true)]
internal sealed class RunPackageAssemblyStepSchemaShape
{
    [StepUiField(Key = "Kraken.Action.PackageRunner.AssemblyPath",
        Widget = StepUiWidgets.Text, Label = "Assembly path",
        Group = "general", Required = true,
        HelpText = "Path to the .dll relative to the package root (e.g. hooks/PostDeploy.dll).",
        Placeholder = "hooks/PostDeploy.dll")]
    public string AssemblyPath { get; set; } = "";

    [StepUiField(Key = "Kraken.Action.PackageRunner.TypeName",
        Widget = StepUiWidgets.Text, Label = "Type name (optional)",
        Group = "general",
        HelpText = "Fully-qualified class name implementing IKrakenStep. Auto-discovered if the assembly has exactly one implementor.")]
    public string TypeName { get; set; } = "";

    [StepUiField(Key = "Kraken.Action.PackageRunner.TimeoutSeconds",
        Widget = StepUiWidgets.NumberInput, Label = "Timeout (seconds)",
        Group = "advanced", Default = "600", Min = 1)]
    public int TimeoutSeconds { get; set; } = 600;
}

// ── Octopus.TerraformPlanDestroy ─────────────────────────────────────────

[StepUiSchemaRoot(Id = "octopus.terraformplandestroy", Title = "Plan a Terraform Destroy",
    Version = "1.0.0",
    Description = "Run terraform plan -destroy to preview resource teardown.")]
[StepUiGroup("general", "General")]
[StepUiGroup("variables", "Variables", Collapsed = true)]
[StepUiGroup("advanced", "Advanced", Collapsed = true)]
internal sealed class TerraformPlanDestroyStepSchemaShape
{
    [StepUiField(Key = "Octopus.Action.Terraform.WorkingDirectory",
        Widget = StepUiWidgets.Text, Label = "Working directory",
        Group = "general")]
    public string WorkingDirectory { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.Workspace",
        Widget = StepUiWidgets.Text, Label = "Workspace",
        Group = "general")]
    public string Workspace { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.PlanFilePath",
        Widget = StepUiWidgets.Text, Label = "Plan output file",
        Group = "general")]
    public string PlanFilePath { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.Vars",
        Widget = StepUiWidgets.Textarea, Label = "Variables",
        Group = "variables",
        HelpText = "One per line: key=value.")]
    public string Vars { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.VarFile",
        Widget = StepUiWidgets.Textarea, Label = "Var files",
        Group = "variables")]
    public string VarFile { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.BackendConfig",
        Widget = StepUiWidgets.Textarea, Label = "Backend config",
        Group = "advanced")]
    public string BackendConfig { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.AdditionalInitArgs",
        Widget = StepUiWidgets.Text, Label = "Additional init args",
        Group = "advanced")]
    public string AdditionalInitArgs { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.AdditionalActionArgs",
        Widget = StepUiWidgets.Text, Label = "Additional plan args",
        Group = "advanced")]
    public string AdditionalActionArgs { get; set; } = "";

    [StepUiField(Key = "Octopus.Action.Terraform.SkipInit",
        Widget = StepUiWidgets.Checkbox, Label = "Skip terraform init",
        Group = "advanced", Default = "false")]
    public bool SkipInit { get; set; }
}
