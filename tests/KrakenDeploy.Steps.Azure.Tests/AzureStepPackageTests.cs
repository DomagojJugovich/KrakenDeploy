using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Azure;

namespace KrakenDeploy.Steps.Azure.Tests;

public sealed class AzureStepPackageTests
{
    private static readonly string[] AllStepTypes =
    [
        "Octopus.AzureWebApp",
        "Octopus.AzureAppService",
        "Octopus.AzurePowerShell",
        "Octopus.AzureResourceGroup",
        "deploy-a-bicep-template",
    ];

    [Theory]
    [InlineData("Octopus.AzureWebApp", true)]
    [InlineData("octopus.azurewebapp", true)]
    [InlineData("Octopus.AzureAppService", true)]
    [InlineData("Octopus.AzurePowerShell", true)]
    [InlineData("Octopus.AzureResourceGroup", true)]
    [InlineData("deploy-a-bicep-template", true)]
    [InlineData("Kraken.Script", false)]
    [InlineData("Octopus.AwsUploadS3", false)]
    [InlineData("", false)]
    public void CanHandle_returns_true_only_for_azure_step_types(string stepType, bool expected)
    {
        var handler = new AzureStepHandler();
        handler.CanHandle(stepType).Should().Be(expected);
    }

    [Fact]
    public void Handler_does_not_require_a_package()
        => new AzureStepHandler().RequiresPackage.Should().BeFalse();

    [Fact]
    public void Handler_handles_all_five_step_types()
    {
        var handler = new AzureStepHandler();
        foreach (var stepType in AllStepTypes)
        {
            handler.CanHandle(stepType).Should().BeTrue($"because {stepType} is an Azure step");
        }
    }

    [Fact]
    public async Task HandleWebApp_fails_when_no_webapp_name()
    {
        var handler = new AzureStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.AzureWebApp", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("WebAppName"));
    }

    [Fact]
    public async Task HandleWebApp_fails_when_no_resource_group()
    {
        var handler = new AzureStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.AzureWebApp", new Dictionary<string, string>
        {
            [AzureConfigKeys.WebAppName] = "myapp",
        }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("ResourceGroupName"));
    }

    [Fact]
    public async Task HandlePowerShell_fails_when_no_script_body()
    {
        var handler = new AzureStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.AzurePowerShell", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("ScriptBody"));
    }

    [Fact]
    public async Task HandleResourceGroup_fails_when_no_resource_group()
    {
        var handler = new AzureStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.AzureResourceGroup", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("ResourceGroupName"));
    }

    [Fact]
    public async Task HandleBicep_fails_when_no_resource_group()
    {
        var handler = new AzureStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("deploy-a-bicep-template", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("ResourceGroupName"));
    }

    [Fact]
    public void Built_archive_exists_at_the_expected_path()
    {
        FindBuiltArchive().Should().NotBeNull(
            "the pack target must produce octopus.azure-<version>.kdeploy-step");
    }

    [Fact]
    public void Built_archive_contains_a_well_formed_manifest_and_executor_DLL()
    {
        var archivePath = FindBuiltArchive();
        archivePath.Should().NotBeNull();

        using var fs = File.OpenRead(archivePath!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var manifestEntry = zip.GetEntry(StepPackageFiles.ManifestFileName);
        manifestEntry.Should().NotBeNull();

        using var reader = new StreamReader(manifestEntry!.Open());
        var manifest = StepPackageManifestJson.Deserialize(reader.ReadToEnd());

        manifest.Id.Should().Be("octopus.azure");
        manifest.Version.Should().Be(ArchiveVersion(FindBuiltArchive()!),
            "the manifest version and the archive filename both come from "
            + "KrakenStepPackageVersion in the csproj and must agree");
        manifest.StepTypes.Should().HaveCount(5);
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.AzureWebApp");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.AzureAppService");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.AzurePowerShell");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.AzureResourceGroup");
        manifest.StepTypes.Should().Contain(t => t.Id == "deploy-a-bicep-template");
        manifest.ExecutorTypeName.Should().Be(typeof(AzureStepHandler).FullName!);
        manifest.ExecutorAssembly.Should().Be("KrakenDeploy.Steps.Azure.dll");

        zip.GetEntry($"executor/{manifest.ExecutorAssembly}").Should().NotBeNull();
    }

    [Fact]
    public void Built_archive_bundles_Steps_Common_and_Octostache_DLLs()
    {
        var archivePath = FindBuiltArchive();
        archivePath.Should().NotBeNull();

        using var fs = File.OpenRead(archivePath!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        zip.GetEntry("executor/KrakenDeploy.Steps.Common.dll").Should().NotBeNull(
            "AzureCliRunner lives in Steps.Common");
        zip.GetEntry("executor/Octostache.dll").Should().NotBeNull(
            "Octostache is used for variable resolution in templates/scripts");
    }

    [Fact]
    public void Built_archive_excludes_agent_hosted_runtime_DLLs()
    {
        var archivePath = FindBuiltArchive();
        archivePath.Should().NotBeNull();

        using var fs = File.OpenRead(archivePath!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entries = zip.Entries.Select(e => e.FullName).ToArray();

        entries.Should().NotContain(e => e.EndsWith("/KrakenDeploy.Contracts.dll", StringComparison.OrdinalIgnoreCase));
        entries.Should().NotContain(e => e.EndsWith("/Google.Protobuf.dll", StringComparison.OrdinalIgnoreCase));
        entries.Should().NotContain(e => e.Contains("/Grpc.", StringComparison.OrdinalIgnoreCase));
    }

    private static StepHandlerContext NewContext(
        string stepType,
        Dictionary<string, string> config,
        List<(string Level, string Message)> logs)
    {
        var plan = new DeploymentPlan(
            DeploymentId: Guid.NewGuid(),
            EnvironmentName: "Production",
            Steps: [],
            Variables: new Dictionary<string, string>(),
            ArrayVariables: new Dictionary<string, string[]>());

        var step = new DeploymentStepPlan(
            Index: 0,
            Name: "Azure",
            StepType: stepType,
            PackageId: "",
            PackageVersion: "",
            Config: config);

        return new StepHandlerContext
        {
            Plan = plan,
            Step = step,
            ExtractDir = "",
            ArtifactsDir = "",
            LogAsync = (level, message) =>
            {
                logs.Add((level, message));
                return Task.CompletedTask;
            },
        };
    }

    private static string? FindBuiltArchive()
    {
        var here = AppContext.BaseDirectory;
        var binRoot = Path.GetFullPath(Path.Combine(
            here, "..", "..", "..", "..", "..",
            "steps", "KrakenDeploy.Steps.Azure", "bin"));
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "octopus.azure-*.kdeploy-step",
                SearchOption.AllDirectories)
                .OrderByDescending(p => Version.Parse(ArchiveVersion(p)))
                .FirstOrDefault()
            : null;
    }

    // "<id>-<version>.kdeploy-step" -> "<version>" (the id itself may contain dashes).
    private static string ArchiveVersion(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem[(stem.LastIndexOf('-') + 1)..];
    }
}
