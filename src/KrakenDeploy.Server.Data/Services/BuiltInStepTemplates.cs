using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.StepTemplates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Seeds Kraken-native step templates that should be available out of the box —
/// distinct from the imported Octopus Library templates. Idempotent: checks
/// for existing entries by ActionType + a sentinel name prefix.
/// <para>
/// Currently seeds:
/// <list type="bullet">
///   <item><c>Kraken.IIS</c> — comprehensive IIS deployment with app-pool, bindings,
///         atomic-swap, and health probe (M9).</item>
///   <item><c>Kraken.Script</c> — Octopus.Script-compatible inline script step
///         with selectable syntax (PowerShell / Bash / CSharp / FSharp / Python)
///         and PowerShell edition (Desktop / Core).</item>
/// </list>
/// </para>
/// </summary>
public class BuiltInStepTemplateSeeder(
    IDbContextFactory<KrakenDbContext> dbFactory,
    ILogger<BuiltInStepTemplateSeeder> logger)
{
    private const string KrakenIisTemplateName = "Kraken.IIS — Deploy Web Site";
    private const string KrakenScriptTemplateName = "Kraken.Script — Run a Script";

    private static readonly (string Name, string ActionType, string Category, string Description)[] CatalogSteps =
    [
        ("Health Check", "Octopus.HealthCheck", "other",
            "Probe an HTTP endpoint or TCP port with retries."),
        ("Transfer a Package", "Octopus.TransferPackage", "package",
            "Copy or upload the deployed package to a file share or HTTP feed endpoint."),
        ("Deploy a Package", "Kraken.DeployPackage", "package",
            "Extract a package to a target directory. Simplified deploy step."),
        ("Run a Docker Container", "Octopus.DockerRun", "docker",
            "Pull and run a container from a registry."),
        ("Stop a Docker Resource", "Octopus.DockerStop", "docker",
            "Stop or remove a container or network created by a previous deployment."),
        ("Create a Docker Network", "Octopus.DockerNetwork", "docker",
            "Create a Docker network for use by containers."),
        ("Deploy Kubernetes YAML", "Octopus.KubernetesDeployRawYaml", "kubernetes",
            "Apply raw YAML manifests to a Kubernetes cluster."),
        ("Deploy Kubernetes Containers", "Octopus.KubernetesDeployContainers", "kubernetes",
            "Create or update a Kubernetes Deployment from a container image."),
        ("Deploy Kubernetes Service", "Octopus.KubernetesDeployService", "kubernetes",
            "Create or update a Kubernetes Service."),
        ("Deploy Kubernetes Ingress", "Octopus.KubernetesDeployIngress", "kubernetes",
            "Create or update a Kubernetes Ingress."),
        ("Deploy Kubernetes ConfigMap", "Octopus.KubernetesDeployConfigMap", "kubernetes",
            "Create or update a Kubernetes ConfigMap."),
        ("Deploy Kubernetes Secret", "Octopus.KubernetesDeploySecret", "kubernetes",
            "Create or update a Kubernetes Secret."),
        ("Deploy with Kustomize", "Octopus.Kubernetes.Kustomize", "kubernetes",
            "Apply a kustomization directory to a Kubernetes cluster."),
        ("Deploy a Helm Chart", "Octopus.HelmChartUpgrade", "kubernetes",
            "Install or upgrade a Helm release on a Kubernetes cluster."),
        ("Run a kubectl Script", "Octopus.KubernetesRunScript", "kubernetes",
            "Run a script with kubectl context authenticated to a Kubernetes cluster."),
        ("Upload to Amazon S3", "Octopus.AwsUploadS3", "aws",
            "Upload files to an Amazon S3 bucket."),
        ("Create an S3 Bucket", "Octopus.AwsCreateS3", "aws",
            "Create a new Amazon S3 bucket."),
        ("Deploy AWS CloudFormation", "Octopus.AwsRunCloudFormation", "aws",
            "Create or update an AWS CloudFormation stack via change sets."),
        ("Apply CloudFormation Change Set", "Octopus.AwsApplyCloudFormationChangeSet", "aws",
            "Execute a previously created CloudFormation change set."),
        ("Delete AWS CloudFormation Stack", "Octopus.AwsDeleteCloudFormation", "aws",
            "Delete an AWS CloudFormation stack and wait for completion."),
        ("Deploy Amazon ECS Service", "aws-ecs", "aws",
            "Update an Amazon ECS service with a new task definition or desired count."),
        ("Update Amazon ECS Service", "aws-ecs-update-service", "aws",
            "Update an Amazon ECS service (alias for aws-ecs)."),
        ("Run an AWS Script", "Octopus.AwsRunScript", "aws",
            "Run a script with AWS credentials set as environment variables."),
        ("Deploy to Azure Web App", "Octopus.AzureWebApp", "azure",
            "Deploy a package to an Azure App Service (Web App)."),
        ("Deploy to Azure App Service", "Octopus.AzureAppService", "azure",
            "Deploy a package to an Azure App Service (alias for AzureWebApp)."),
        ("Run an Azure PowerShell Script", "Octopus.AzurePowerShell", "azure",
            "Run a PowerShell script with Azure credentials set as environment variables."),
        ("Deploy an Azure ARM Template", "Octopus.AzureResourceGroup", "azure",
            "Deploy an Azure Resource Manager (ARM) template to a resource group."),
        ("Deploy a Bicep Template", "deploy-a-bicep-template", "azure",
            "Deploy an Azure Bicep template to a resource group."),
        ("Deploy a Java Archive", "Octopus.JavaArchive", "java",
            "Deploy a WAR, JAR, or EAR file to a target directory."),
        ("Deploy to Tomcat", "Octopus.TomcatDeploy", "java",
            "Deploy a WAR file to an Apache Tomcat instance."),
        ("Manage Tomcat State", "Octopus.TomcatState", "java",
            "Start, stop, or restart an Apache Tomcat instance."),
        ("Deploy Certificate to Tomcat", "Octopus.TomcatDeployCertificate", "java",
            "Import a certificate into a Tomcat Java keystore."),
        ("Deploy to WildFly", "Octopus.WildFlyDeploy", "java",
            "Deploy a WAR or EAR to a WildFly/JBoss instance via CLI."),
        ("Manage WildFly State", "Octopus.WildFlyState", "java",
            "Start, stop, restart, or reload a WildFly/JBoss instance."),
        ("Deploy Certificate to WildFly", "Octopus.WildFlyCertificateDeploy", "java",
            "Import a certificate into a WildFly keystore."),
        ("Deploy a Java Certificate", "Octopus.JavaDeployCertificate", "java",
            "Import a certificate into a generic Java keystore via keytool."),
        ("Apply a Terraform Template", "Octopus.TerraformApply", "terraform",
            "Run terraform init and apply to provision infrastructure."),
        ("Plan a Terraform Deployment", "Octopus.TerraformPlan", "terraform",
            "Run terraform init and plan to preview infrastructure changes."),
        ("Destroy Terraform Resources", "Octopus.TerraformDestroy", "terraform",
            "Run terraform init and destroy to tear down infrastructure."),
        ("Plan a Terraform Destroy", "Octopus.TerraformPlanDestroy", "terraform",
            "Run terraform plan -destroy to preview resource teardown."),
        ("Send an Email", "Octopus.Email", "other",
            "Send an email notification via SMTP."),
        ("Manage Nginx", "Octopus.Nginx", "nginx",
            "Write an nginx config and reload/restart the service."),
        ("Import a Certificate", "Octopus.Certificate.Import", "certificate",
            "Import a certificate into the Windows certificate store."),
        ("Transfer a VHD", "Octopus.Vhd", "other",
            "Copy or expand a VHD/VHDX disk image."),
        ("Run a Package Executable", "Kraken.RunPackageExecutable", "script",
            "Run an executable or binary shipped inside the deployment package."),
        ("Run a .NET Assembly Step", "Kraken.RunPackageAssembly", "script",
            "Load a .NET DLL from the package and invoke its IKrakenStep implementation."),
    ];

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var iisExisting = await db.StepTemplates
            .FirstOrDefaultAsync(t => t.Name == KrakenIisTemplateName, ct)
            .ConfigureAwait(false);

        if (iisExisting is null)
        {
            var template = BuildKrakenIisTemplate();
            db.StepTemplates.Add(template);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation(
                "Seeded built-in step template '{Name}'.", KrakenIisTemplateName);
        }

        var scriptExisting = await db.StepTemplates
            .FirstOrDefaultAsync(t => t.Name == KrakenScriptTemplateName, ct)
            .ConfigureAwait(false);

        if (scriptExisting is null)
        {
            var template = BuildKrakenScriptTemplate();
            db.StepTemplates.Add(template);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation(
                "Seeded built-in step template '{Name}'.", KrakenScriptTemplateName);
        }

        var existingActionTypes = await db.StepTemplates
            .Where(t => t.Source == StepTemplateSource.BuiltIn)
            .Select(t => t.ActionType)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var existingSet = new HashSet<string>(existingActionTypes, StringComparer.OrdinalIgnoreCase);

        var seeded = 0;
        foreach (var (name, actionType, category, description) in CatalogSteps)
        {
            if (existingSet.Contains(actionType))
            {
                continue;
            }

            db.StepTemplates.Add(new StepTemplate
            {
                Name = name,
                ActionType = actionType,
                Source = StepTemplateSource.BuiltIn,
                Category = category,
                Author = "KrakenDeploy",
                Description = description,
                Properties = [],
                Parameters = [],
            });
            existingSet.Add(actionType);
            seeded++;
        }

        if (seeded > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Seeded {Count} built-in step template(s) from catalog.", seeded);
        }
    }

    // ── Kraken.IIS template definition ─────────────────────────────────────────

    private static StepTemplate BuildKrakenIisTemplate()
    {
        return new StepTemplate
        {
            Name        = KrakenIisTemplateName,
            ActionType  = "Kraken.IIS",
            Source      = StepTemplateSource.BuiltIn,
            Category    = "iis",
            Author      = "KrakenDeploy",
            Description =
                "Comprehensive IIS deployment: ensures the app pool with full process-model, " +
                "recycling, and rapid-fail settings; ensures the site and replaces bindings " +
                "(SNI + certificate from store); performs an atomic-swap deploy with versioned " +
                "directories and retention; recycles the pool in drain mode; runs an optional " +
                "post-deploy HTTP health probe with retries.",
            Properties  = [],
            Parameters  =
            [
                // ── General ────────────────────────────────────────────────────
                Param(KrakenIisConfigKeys.SiteName, "Site name",
                    "IIS site name. Created if missing.", required: true),
                Param(KrakenIisConfigKeys.WebRoot, "Web root path",
                    @"Filesystem path. In atomic-swap mode (default) versioned subdirectories are created underneath, e.g. C:\inetpub\my-site\v-2025.04.27-153000\.",
                    required: true),
                Param(KrakenIisConfigKeys.AppPath, "Application path",
                    "Virtual path within the site (default: /).",
                    defaultValue: "/"),

                // ── App Pool ──────────────────────────────────────────────────
                Param(KrakenIisConfigKeys.AppPoolName, "App pool name",
                    "App pool name (default: same as site name)."),
                Select(KrakenIisConfigKeys.AppPoolRuntimeVersion, "Managed runtime version",
                    "v4.0",
                    ["v4.0|v4.0", "|No Managed Code (e.g. .NET Core/5+)"]),
                Select(KrakenIisConfigKeys.AppPoolPipelineMode, "Pipeline mode",
                    "Integrated",
                    ["Integrated|Integrated", "Classic|Classic"]),
                Checkbox(KrakenIisConfigKeys.AppPoolEnable32Bit, "Enable 32-bit applications",
                    "Allow 32-bit IIS modules to load. Required for some legacy components.",
                    defaultValue: "false"),
                Checkbox(KrakenIisConfigKeys.AppPoolLoadUserProfile, "Load user profile",
                    "Load the user profile of the app-pool identity. Required when code reads user-scoped settings or temp paths.",
                    defaultValue: "false"),
                Select(KrakenIisConfigKeys.AppPoolIdentityType, "Identity",
                    "ApplicationPoolIdentity",
                    [
                        "ApplicationPoolIdentity|ApplicationPoolIdentity",
                        "LocalSystem|LocalSystem",
                        "LocalService|LocalService",
                        "NetworkService|NetworkService",
                        "SpecificUser|SpecificUser",
                    ]),
                Param(KrakenIisConfigKeys.AppPoolUsername, "Username (SpecificUser)",
                    @"Required when Identity = SpecificUser. Format: DOMAIN\user or .\user."),
                Sensitive(KrakenIisConfigKeys.AppPoolPassword, "Password (SpecificUser)",
                    "Required when Identity = SpecificUser. Stored encrypted in the deployment plan."),
                Param(KrakenIisConfigKeys.AppPoolIdleTimeoutMin, "Idle timeout (minutes)",
                    "Minutes of inactivity before the app pool is unloaded. 0 = never.",
                    defaultValue: "20"),
                Select(KrakenIisConfigKeys.AppPoolStartMode, "Start mode",
                    "OnDemand",
                    [
                        "OnDemand|OnDemand (start when first request arrives)",
                        "AlwaysRunning|AlwaysRunning (warm on app-pool start)",
                    ]),

                // ── Recycling ─────────────────────────────────────────────────
                Param(KrakenIisConfigKeys.RecycleRegularInterval, "Recycle interval (minutes)",
                    "Regular recycle interval. 0 = never. Default 1740 = 29 hours (IIS default).",
                    defaultValue: "1740"),
                Param(KrakenIisConfigKeys.RecyclePrivateMemoryKB, "Private-memory limit (KB)",
                    "Recycle when private memory exceeds this many KB. Blank = no limit."),
                Param(KrakenIisConfigKeys.RecycleVirtualMemoryKB, "Virtual-memory limit (KB)",
                    "Recycle when virtual memory exceeds this many KB. Blank = no limit."),
                Param(KrakenIisConfigKeys.RecycleRequestLimit, "Request limit",
                    "Recycle after this many requests. Blank = no limit."),
                Param(KrakenIisConfigKeys.RecycleSpecificTimes, "Specific recycle times",
                    "Semicolon-separated HH:mm times (e.g. 02:00;14:00)."),
                Checkbox(KrakenIisConfigKeys.RecycleLogEventTime, "Log event: Time",
                    "Write an event-log entry when the pool recycles on the regular interval.",
                    defaultValue: "true"),
                Checkbox(KrakenIisConfigKeys.RecycleLogEventMemory, "Log event: Memory",
                    "Write an event-log entry on memory-limit recycles.",
                    defaultValue: "true"),
                Checkbox(KrakenIisConfigKeys.RecycleLogEventRequests, "Log event: Requests",
                    "Write an event-log entry on request-count recycles.",
                    defaultValue: "true"),
                Checkbox(KrakenIisConfigKeys.RecycleLogEventSchedule, "Log event: Schedule",
                    "Write an event-log entry on scheduled-time recycles.",
                    defaultValue: "true"),
                Checkbox(KrakenIisConfigKeys.RecycleLogEventConfig, "Log event: Config change",
                    "Write an event-log entry when configuration changes trigger a recycle.",
                    defaultValue: "true"),
                Checkbox(KrakenIisConfigKeys.RecycleLogEventIsapi, "Log event: ISAPI",
                    "Write an event-log entry on ISAPI-requested recycles.",
                    defaultValue: "true"),
                Checkbox(KrakenIisConfigKeys.RecycleLogEventOnDemand, "Log event: On-demand",
                    "Write an event-log entry on manual recycles.",
                    defaultValue: "true"),

                // ── Rapid-fail Protection ─────────────────────────────────────
                Checkbox(KrakenIisConfigKeys.RapidFailEnabled, "Rapid-fail protection",
                    "Disable the app pool after too many crashes in a short interval.",
                    defaultValue: "true"),
                Param(KrakenIisConfigKeys.RapidFailMaxCrashes, "Max crashes per interval",
                    "Default: 5.", defaultValue: "5"),
                Param(KrakenIisConfigKeys.RapidFailIntervalMinutes, "Crash window (minutes)",
                    "Default: 5.", defaultValue: "5"),

                // ── Bindings ──────────────────────────────────────────────────
                MultiLine(KrakenIisConfigKeys.Bindings, "Bindings",
                    "One per line: protocol|ip|port|hostname|certThumbprint|certStore|sniRequired|sslFlags. " +
                    "Examples:\n" +
                    "http|*|80\n" +
                    "https|*|443|app.example.com|#{App.SslThumbprint}|My|true|1"),

                // ── Preload / AlwaysRunning ───────────────────────────────────
                Checkbox(KrakenIisConfigKeys.PreloadEnabled, "Application preload",
                    "Send a fake request to warm the application after start/recycle.",
                    defaultValue: "false"),
                Checkbox(KrakenIisConfigKeys.AlwaysRunning, "Always running (sets pool startMode)",
                    "Equivalent to setting AppPool.StartMode = AlwaysRunning.",
                    defaultValue: "false"),

                // ── Deploy Strategy ───────────────────────────────────────────
                Select(KrakenIisConfigKeys.DeployMode, "Deploy mode",
                    "AtomicSwap",
                    [
                        "AtomicSwap|AtomicSwap (versioned dir + physicalPath swap)",
                        "InPlace|InPlace (overwrite webRoot)",
                    ]),
                Param(KrakenIisConfigKeys.DeployKeepVersions, "Keep N old versions",
                    "AtomicSwap only. 0 = keep all.",
                    defaultValue: "5"),
                Checkbox(KrakenIisConfigKeys.DeployDrainMode, "Drain-mode recycle",
                    "Use overlapping recycle so existing requests complete on the old worker.",
                    defaultValue: "true"),

                // ── Health Probe ──────────────────────────────────────────────
                Param(KrakenIisConfigKeys.HealthCheckUrl, "Health probe URL",
                    "Optional. Absolute URL probed after deploy. Leave blank to skip."),
                Param(KrakenIisConfigKeys.HealthCheckExpectedStatus, "Expected status code",
                    defaultValue: "200"),
                Param(KrakenIisConfigKeys.HealthCheckTimeoutSeconds, "Timeout (seconds)",
                    defaultValue: "30"),
                Param(KrakenIisConfigKeys.HealthCheckRetryAttempts, "Retry attempts",
                    defaultValue: "5"),
                Param(KrakenIisConfigKeys.HealthCheckRetryDelaySeconds, "Retry delay (seconds)",
                    defaultValue: "3"),
                Param(KrakenIisConfigKeys.HealthCheckExpectedBodyContains, "Expected body fragment",
                    "Optional substring that must appear in the response body."),
            ],
        };
    }

    // ── Kraken.Script template definition ──────────────────────────────────────

    private static StepTemplate BuildKrakenScriptTemplate()
    {
        return new StepTemplate
        {
            Name        = KrakenScriptTemplateName,
            ActionType  = "Kraken.Script",
            Source      = StepTemplateSource.BuiltIn,
            Category    = "script",
            Author      = "KrakenDeploy",
            Description =
                "Runs an inline script on the deployment target. Drop-in compatible " +
                "with the Octopus.Script parameter contract: " +
                "`Octopus.Action.Script.Syntax`, `Octopus.Action.Script.ScriptBody`, " +
                "and `Octopus.Action.PowerShell.Edition`. Variable expressions are " +
                "substituted before the script runs.",
            Properties  = [],
            Parameters  =
            [
                Select(KrakenScriptConfigKeys.Syntax, "Script syntax",
                    "PowerShell",
                    [
                        "PowerShell|PowerShell",
                        "Bash|Bash",
                        "CSharp|C# (dotnet-script)",
                        "FSharp|F# (dotnet fsi)",
                        "Python|Python",
                    ]),
                Select(KrakenScriptConfigKeys.PowerShellEdition, "PowerShell edition",
                    "Desktop",
                    [
                        "Desktop|Desktop (Windows PowerShell 5.x)",
                        "Core|Core (pwsh 7+)",
                    ]),
                MultiLine(KrakenScriptConfigKeys.ScriptBody, "Script body",
                    "Inline script source. Variable expressions like #{MyVar} are " +
                    "substituted server-side before execution."),
            ],
        };
    }

    // ── Parameter constructors (small DSL to keep the seed data readable) ──────

    private static StepTemplateParameter Param(
        string name, string label, string? help = null,
        string? defaultValue = null, bool required = false)
    {
        var helpText = required && string.IsNullOrEmpty(help)
            ? "Required."
            : (required ? help + " (Required.)" : help);

        return new StepTemplateParameter
        {
            Name         = name,
            Label        = label,
            HelpText     = helpText,
            DefaultValue = defaultValue,
            ControlType  = "SingleLineText",
        };
    }

    private static StepTemplateParameter MultiLine(string name, string label, string? help = null) =>
        new()
        {
            Name        = name,
            Label       = label,
            HelpText    = help,
            ControlType = "MultiLineText",
        };

    private static StepTemplateParameter Checkbox(
        string name, string label, string? help = null, string defaultValue = "false") =>
        new()
        {
            Name         = name,
            Label        = label,
            HelpText     = help,
            DefaultValue = defaultValue,
            ControlType  = "Checkbox",
        };

    private static StepTemplateParameter Sensitive(string name, string label, string? help = null) =>
        new()
        {
            Name        = name,
            Label       = label,
            HelpText    = help,
            ControlType = "Sensitive",
        };

    private static StepTemplateParameter Select(
        string name, string label,
        string defaultValue, string[] options) =>
        new()
        {
            Name          = name,
            Label         = label,
            DefaultValue  = defaultValue,
            ControlType   = "Select",
            SelectOptions = [.. options],
        };
}
