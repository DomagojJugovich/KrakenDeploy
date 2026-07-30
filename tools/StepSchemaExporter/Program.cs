using KrakenDeploy.Contracts.Steps;

// SC1-c: export BuiltInStepSchemas into per-type ui-schemas/{typeId}.json
// files inside each owning steps/ project. Run from the repo root. Idempotent
// (overwrites). Claimed types with no built-in schema are reported and left
// for hand-authoring (e.g. Octopus.WindowsService, which never had one).

var stepsRoot = Path.Combine(Directory.GetCurrentDirectory(), "steps");
if (!Directory.Exists(stepsRoot))
{
    Console.Error.WriteLine($"No steps/ directory under '{Directory.GetCurrentDirectory()}' — run from the repo root.");
    return 1;
}

// Project → claimed step types, verbatim from each csproj's declaration.
// Kept in sync by the SC1-d lint test, which cross-checks built archives.
var claims = new Dictionary<string, string[]>
{
    ["KrakenDeploy.Steps.Aws"] =
    [
        "Octopus.AwsUploadS3", "Octopus.AwsCreateS3", "Octopus.AwsRunCloudFormation",
        "Octopus.AwsApplyCloudFormationChangeSet", "Octopus.AwsDeleteCloudFormation",
        "aws-ecs", "aws-ecs-update-service", "Octopus.AwsRunScript",
    ],
    ["KrakenDeploy.Steps.Azure"] =
    [
        "Octopus.AzureWebApp", "Octopus.AzureAppService", "Octopus.AzurePowerShell",
        "Octopus.AzureResourceGroup", "deploy-a-bicep-template",
    ],
    ["KrakenDeploy.Steps.Docker"] =
    [
        "Octopus.DockerRun", "Octopus.DockerStop", "Octopus.DockerNetwork",
    ],
    ["KrakenDeploy.Steps.HealthCheck"] = ["Octopus.HealthCheck"],
    ["KrakenDeploy.Steps.Java"] =
    [
        "Octopus.JavaArchive", "Octopus.TomcatDeploy", "Octopus.TomcatState",
        "Octopus.TomcatDeployCertificate", "Octopus.WildFlyDeploy", "Octopus.WildFlyState",
        "Octopus.WildFlyCertificateDeploy", "Octopus.JavaDeployCertificate",
    ],
    ["KrakenDeploy.Steps.JsonConfigurationVariables"] = ["Octopus.JsonConfigurationVariables"],
    ["KrakenDeploy.Steps.KrakenIis"] = ["Kraken.IIS", "Octopus.IIS"],
    ["KrakenDeploy.Steps.Kubernetes"] =
    [
        "Octopus.KubernetesDeployRawYaml", "Octopus.KubernetesDeployContainers",
        "Octopus.KubernetesDeployService", "Octopus.KubernetesDeployIngress",
        "Octopus.KubernetesDeployConfigMap", "Octopus.KubernetesDeploySecret",
        "Octopus.Kubernetes.Kustomize", "Octopus.HelmChartUpgrade", "Octopus.KubernetesRunScript",
    ],
    ["KrakenDeploy.Steps.Manual"] = ["Octopus.Manual"],
    ["KrakenDeploy.Steps.Misc"] =
    [
        "Octopus.Email", "Octopus.Nginx", "Octopus.Certificate.Import", "Octopus.Vhd",
    ],
    ["KrakenDeploy.Steps.OctopusTentaclePackage"] = ["Octopus.TentaclePackage", "Kraken.DeployPackage"],
    ["KrakenDeploy.Steps.OctopusWindowsService"] = ["Octopus.WindowsService"],
    ["KrakenDeploy.Steps.PackageRunner"] = ["Kraken.RunPackageExecutable", "Kraken.RunPackageAssembly"],
    ["KrakenDeploy.Steps.Script"] = ["Kraken.Script", "Octopus.Script"],
    ["KrakenDeploy.Steps.SubstituteVariables"] = ["Octopus.SubstituteVariables"],
    ["KrakenDeploy.Steps.Terraform"] =
    [
        "Octopus.TerraformApply", "Octopus.TerraformPlan",
        "Octopus.TerraformDestroy", "Octopus.TerraformPlanDestroy",
    ],
    ["KrakenDeploy.Steps.TransferPackage"] = ["Octopus.TransferPackage"],
};

var written = 0;
var missing = new List<string>();

foreach (var (project, types) in claims)
{
    var projectDir = Path.Combine(stepsRoot, project);
    if (!Directory.Exists(projectDir))
    {
        Console.Error.WriteLine($"Project directory not found: {projectDir}");
        return 1;
    }

    var schemasDir = Path.Combine(projectDir, "ui-schemas");
    Directory.CreateDirectory(schemasDir);

    foreach (var typeId in types)
    {
        var schema = BuiltInStepSchemas.GetForStepType(typeId);
        if (schema is null)
        {
            missing.Add($"{project}: {typeId}");
            continue;
        }

        var path = Path.Combine(schemasDir, $"{typeId.ToLowerInvariant()}.json");
        File.WriteAllText(path, StepUiSchemaJson.Serialize(schema) + Environment.NewLine);
        written++;
        Console.WriteLine($"wrote {Path.GetRelativePath(Directory.GetCurrentDirectory(), path)}");
    }
}

Console.WriteLine($"\n{written} schema file(s) written.");
if (missing.Count > 0)
{
    Console.WriteLine($"{missing.Count} claimed type(s) have NO built-in schema (hand-author these):");
    foreach (var m in missing) { Console.WriteLine($"  - {m}"); }
}

return 0;
