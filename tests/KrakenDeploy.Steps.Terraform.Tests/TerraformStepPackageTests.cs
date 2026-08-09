using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Terraform;

namespace KrakenDeploy.Steps.Terraform.Tests;

public sealed class TerraformStepPackageTests
{
    private static readonly string[] AllStepTypes =
    [
        "Octopus.TerraformApply",
        "Octopus.TerraformPlan",
        "Octopus.TerraformDestroy",
        "Octopus.TerraformPlanDestroy",
    ];

    [Theory]
    [InlineData("Octopus.TerraformApply", true)]
    [InlineData("octopus.terraformapply", true)]
    [InlineData("Octopus.TerraformPlan", true)]
    [InlineData("Octopus.TerraformDestroy", true)]
    [InlineData("Octopus.TerraformPlanDestroy", true)]
    [InlineData("Kraken.Script", false)]
    [InlineData("Octopus.DockerRun", false)]
    [InlineData("", false)]
    public void CanHandle_returns_true_only_for_terraform_step_types(string stepType, bool expected)
    {
        var handler = new TerraformStepHandler();
        handler.CanHandle(stepType).Should().Be(expected);
    }

    [Fact]
    public void Handler_does_not_require_a_package()
        => new TerraformStepHandler().RequiresPackage.Should().BeFalse();

    [Fact]
    public void Handler_handles_all_four_step_types()
    {
        var handler = new TerraformStepHandler();
        foreach (var stepType in AllStepTypes)
        {
            handler.CanHandle(stepType).Should().BeTrue($"because {stepType} is a Terraform step");
        }
    }

    [Fact]
    public async Task HandleAsync_fails_when_no_working_directory()
    {
        var handler = new TerraformStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.TerraformApply", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("working directory"));
    }

    [Fact]
    public async Task HandleAsync_fails_when_working_directory_has_no_tf_files()
    {
        var handler = new TerraformStepHandler();
        var logs = new List<(string Level, string Message)>();

        var emptyDir = Path.Combine(Path.GetTempPath(), $"kraken-tf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);
        await File.WriteAllTextAsync(Path.Combine(emptyDir, "readme.txt"), "no tf here");

        try
        {
            var context = NewContext("Octopus.TerraformPlan", new Dictionary<string, string>
            {
                [TerraformConfigKeys.WorkingDirectory] = emptyDir,
            }, logs);

            var success = await handler.HandleAsync(context, CancellationToken.None);

            success.Should().BeFalse();
            logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("working directory"));
        }
        finally
        {
            Directory.Delete(emptyDir, true);
        }
    }

    [Fact]
    public async Task HandleAsync_finds_tf_files_in_extract_dir()
    {
        var handler = new TerraformStepHandler();
        var logs = new List<(string Level, string Message)>();

        var extractDir = Path.Combine(Path.GetTempPath(), $"kraken-tf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        await File.WriteAllTextAsync(Path.Combine(extractDir, "main.tf"), "resource \"null_resource\" \"test\" {}");

        try
        {
            var context = NewContext("Octopus.TerraformApply", new Dictionary<string, string>
            {
                [TerraformConfigKeys.SkipInit] = "True",
            }, logs, extractDir);

            try
            {
                await handler.HandleAsync(context, CancellationToken.None);
            }
            catch
            {
                // terraform binary not installed on CI — the handler got past
                // working-directory resolution, which is what we're testing.
            }

            logs.Should().NotContain(l => l.Level == "error" && l.Message.Contains("working directory"));
        }
        finally
        {
            Directory.Delete(extractDir, true);
        }
    }

    [Fact]
    public void Built_archive_exists_at_the_expected_path()
    {
        FindBuiltArchive().Should().NotBeNull(
            "the pack target must produce octopus.terraform-<version>.kdeploy-step");
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

        manifest.Id.Should().Be("octopus.terraform");
        manifest.Version.Should().Be(ArchiveVersion(FindBuiltArchive()!),
            "the manifest version and the archive filename both come from "
            + "KrakenStepPackageVersion in the csproj and must agree");
        manifest.StepTypes.Should().HaveCount(4);
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.TerraformApply");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.TerraformPlan");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.TerraformDestroy");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.TerraformPlanDestroy");
        manifest.ExecutorTypeName.Should().Be(typeof(TerraformStepHandler).FullName!);
        manifest.ExecutorAssembly.Should().Be("KrakenDeploy.Steps.Terraform.dll");

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
            "TerraformCliRunner lives in Steps.Common");
        zip.GetEntry("executor/Octostache.dll").Should().NotBeNull(
            "Octostache is used for variable resolution");
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
        List<(string Level, string Message)> logs,
        string extractDir = "")
    {
        var plan = new DeploymentPlan(
            DeploymentId: Guid.NewGuid(),
            EnvironmentName: "Production",
            Steps: [],
            Variables: new Dictionary<string, string>(),
            ArrayVariables: new Dictionary<string, string[]>());

        var step = new DeploymentStepPlan(
            Index: 0,
            Name: "Terraform",
            StepType: stepType,
            PackageId: "",
            PackageVersion: "",
            Config: config);

        return new StepHandlerContext
        {
            Plan = plan,
            Step = step,
            ExtractDir = extractDir,
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
            "steps", "KrakenDeploy.Steps.Terraform", "bin"));
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "octopus.terraform-*.kdeploy-step",
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
