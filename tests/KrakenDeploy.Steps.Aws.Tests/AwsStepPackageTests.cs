using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Aws;

namespace KrakenDeploy.Steps.Aws.Tests;

public sealed class AwsStepPackageTests
{
    private static readonly string[] AllStepTypes =
    [
        "Octopus.AwsUploadS3",
        "Octopus.AwsCreateS3",
        "Octopus.AwsRunCloudFormation",
        "Octopus.AwsApplyCloudFormationChangeSet",
        "Octopus.AwsDeleteCloudFormation",
        "aws-ecs",
        "aws-ecs-update-service",
        "Octopus.AwsRunScript",
    ];

    [Theory]
    [InlineData("Octopus.AwsUploadS3", true)]
    [InlineData("octopus.awsuploads3", true)]
    [InlineData("Octopus.AwsCreateS3", true)]
    [InlineData("Octopus.AwsRunCloudFormation", true)]
    [InlineData("Octopus.AwsApplyCloudFormationChangeSet", true)]
    [InlineData("Octopus.AwsDeleteCloudFormation", true)]
    [InlineData("aws-ecs", true)]
    [InlineData("aws-ecs-update-service", true)]
    [InlineData("Octopus.AwsRunScript", true)]
    [InlineData("Kraken.Script", false)]
    [InlineData("Octopus.DockerRun", false)]
    [InlineData("", false)]
    public void CanHandle_returns_true_only_for_aws_step_types(string stepType, bool expected)
    {
        var handler = new AwsStepHandler();
        handler.CanHandle(stepType).Should().Be(expected);
    }

    [Fact]
    public void Handler_does_not_require_a_package()
        => new AwsStepHandler().RequiresPackage.Should().BeFalse();

    [Fact]
    public void Handler_handles_all_eight_step_types()
    {
        var handler = new AwsStepHandler();
        foreach (var stepType in AllStepTypes)
        {
            handler.CanHandle(stepType).Should().BeTrue($"because {stepType} is an AWS step");
        }
    }

    [Fact]
    public async Task HandleUploadS3_fails_when_no_bucket()
    {
        var handler = new AwsStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.AwsUploadS3", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("BucketName"));
    }

    [Fact]
    public async Task HandleCreateS3_fails_when_no_bucket()
    {
        var handler = new AwsStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.AwsCreateS3", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("BucketName"));
    }

    [Fact]
    public async Task HandleRunCloudFormation_fails_when_no_stack_name()
    {
        var handler = new AwsStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.AwsRunCloudFormation", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("StackName"));
    }

    [Fact]
    public async Task HandleApplyChangeSet_fails_when_missing_required_fields()
    {
        var handler = new AwsStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.AwsApplyCloudFormationChangeSet",
            new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("StackName"));
    }

    [Fact]
    public async Task HandleDeleteCloudFormation_fails_when_no_stack_name()
    {
        var handler = new AwsStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.AwsDeleteCloudFormation", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("StackName"));
    }

    [Fact]
    public async Task HandleEcs_fails_when_no_cluster()
    {
        var handler = new AwsStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("aws-ecs", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("ClusterName"));
    }

    [Fact]
    public async Task HandleRunScript_fails_when_no_script_body()
    {
        var handler = new AwsStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.AwsRunScript", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("ScriptBody"));
    }

    [Fact]
    public void Built_archive_exists_at_the_expected_path()
    {
        FindBuiltArchive().Should().NotBeNull(
            "the pack target must produce octopus.aws-<version>.kdeploy-step");
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

        manifest.Id.Should().Be("octopus.aws");
        manifest.Version.Should().Be(ArchiveVersion(FindBuiltArchive()!),
            "the manifest version and the archive filename both come from "
            + "KrakenStepPackageVersion in the csproj and must agree");
        manifest.StepTypes.Should().HaveCount(8);
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.AwsUploadS3");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.AwsCreateS3");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.AwsRunCloudFormation");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.AwsApplyCloudFormationChangeSet");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.AwsDeleteCloudFormation");
        manifest.StepTypes.Should().Contain(t => t.Id == "aws-ecs");
        manifest.StepTypes.Should().Contain(t => t.Id == "aws-ecs-update-service");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.AwsRunScript");
        manifest.ExecutorTypeName.Should().Be(typeof(AwsStepHandler).FullName!);
        manifest.ExecutorAssembly.Should().Be("KrakenDeploy.Steps.Aws.dll");

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
            "AwsCliRunner lives in Steps.Common");
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
            Name: "AWS",
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
            "steps", "KrakenDeploy.Steps.Aws", "bin"));
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "octopus.aws-*.kdeploy-step",
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
