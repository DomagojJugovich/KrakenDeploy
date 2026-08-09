using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Docker;

namespace KrakenDeploy.Steps.Docker.Tests;

public sealed class DockerStepPackageTests
{
    [Theory]
    [InlineData("Octopus.DockerRun", true)]
    [InlineData("octopus.dockerrun", true)]
    [InlineData("Octopus.DockerStop", true)]
    [InlineData("Octopus.DockerNetwork", true)]
    [InlineData("Kraken.Script", false)]
    [InlineData("", false)]
    public void CanHandle_returns_true_only_for_docker_step_types(string stepType, bool expected)
    {
        var handler = new DockerStepHandler();
        handler.CanHandle(stepType).Should().Be(expected);
    }

    [Fact]
    public void Handler_does_not_require_a_package()
        => new DockerStepHandler().RequiresPackage.Should().BeFalse();

    [Fact]
    public async Task HandleRunAsync_fails_when_no_image_configured()
    {
        var handler = new DockerStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.DockerRun", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("Image is required"));
    }

    [Fact]
    public async Task HandleStopAsync_fails_when_no_resource_name()
    {
        var handler = new DockerStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.DockerStop", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("ResourceName"));
    }

    [Fact]
    public async Task HandleNetworkAsync_fails_when_no_network_name()
    {
        var handler = new DockerStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.DockerNetwork", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("NetworkName"));
    }

    [Fact]
    public void Built_archive_exists_at_the_expected_path()
    {
        FindBuiltArchive().Should().NotBeNull(
            "the pack target must produce octopus.docker-<version>.kdeploy-step");
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

        manifest.Id.Should().Be("octopus.docker");
        manifest.Version.Should().Be(ArchiveVersion(FindBuiltArchive()!),
            "the manifest version and the archive filename both come from "
            + "KrakenStepPackageVersion in the csproj and must agree");
        manifest.StepTypes.Should().HaveCount(3);
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.DockerRun");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.DockerStop");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.DockerNetwork");
        manifest.ExecutorTypeName.Should().Be(typeof(DockerStepHandler).FullName!);
        manifest.ExecutorAssembly.Should().Be("KrakenDeploy.Steps.Docker.dll");

        zip.GetEntry($"executor/{manifest.ExecutorAssembly}").Should().NotBeNull();
    }

    [Fact]
    public void Built_archive_bundles_Steps_Common_runtime_DLL()
    {
        var archivePath = FindBuiltArchive();
        archivePath.Should().NotBeNull();

        using var fs = File.OpenRead(archivePath!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        zip.GetEntry("executor/KrakenDeploy.Steps.Common.dll").Should().NotBeNull(
            "DockerCliRunner lives in Steps.Common and must ship in the archive");
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
            Name: "Docker",
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
            "steps", "KrakenDeploy.Steps.Docker", "bin"));
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "octopus.docker-*.kdeploy-step",
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
