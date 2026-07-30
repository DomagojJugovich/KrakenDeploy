using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.PackageRunner;

namespace KrakenDeploy.Steps.PackageRunner.Tests;

public sealed class PackageRunnerStepPackageTests
{
    private static readonly string[] AllStepTypes =
    [
        "Kraken.RunPackageExecutable",
        "Kraken.RunPackageAssembly",
    ];

    [Theory]
    [InlineData("Kraken.RunPackageExecutable", true)]
    [InlineData("kraken.runpackageexecutable", true)]
    [InlineData("Kraken.RunPackageAssembly", true)]
    [InlineData("kraken.runpackageassembly", true)]
    [InlineData("Kraken.Script", false)]
    [InlineData("Octopus.DockerRun", false)]
    [InlineData("", false)]
    public void CanHandle_returns_true_only_for_packagerunner_step_types(string stepType, bool expected)
    {
        var handler = new PackageRunnerStepHandler();
        handler.CanHandle(stepType).Should().Be(expected);
    }

    [Fact]
    public void Handler_requires_a_package()
        => new PackageRunnerStepHandler().RequiresPackage.Should().BeTrue(
            "both EXE and DLL modes need the deployed package extracted on disk");

    [Fact]
    public void Handler_handles_both_step_types()
    {
        var handler = new PackageRunnerStepHandler();
        foreach (var stepType in AllStepTypes)
        {
            handler.CanHandle(stepType).Should().BeTrue($"because {stepType} is a PackageRunner step");
        }
    }

    [Fact]
    public async Task HandleExecutable_fails_when_no_executable_path()
    {
        var handler = new PackageRunnerStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Kraken.RunPackageExecutable", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("ExecutablePath"));
    }

    [Fact]
    public async Task HandleExecutable_fails_when_file_not_found()
    {
        var handler = new PackageRunnerStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Kraken.RunPackageExecutable", new Dictionary<string, string>
        {
            [PackageRunnerConfigKeys.ExecutablePath] = "nonexistent.exe",
        }, logs, "C:\\fake-extract");

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("not found"));
    }

    [Fact]
    public async Task HandleAssembly_fails_when_no_assembly_path()
    {
        var handler = new PackageRunnerStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Kraken.RunPackageAssembly", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("AssemblyPath"));
    }

    [Fact]
    public async Task HandleAssembly_fails_when_file_not_found()
    {
        var handler = new PackageRunnerStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Kraken.RunPackageAssembly", new Dictionary<string, string>
        {
            [PackageRunnerConfigKeys.AssemblyPath] = "nonexistent.dll",
        }, logs, "C:\\fake-extract");

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("not found"));
    }

    [Fact]
    public async Task HandleAssembly_fails_when_assembly_is_not_a_valid_dll()
    {
        var handler = new PackageRunnerStepHandler();
        var logs = new List<(string Level, string Message)>();

        var extractDir = Path.Combine(Path.GetTempPath(), $"kraken-pr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        await File.WriteAllTextAsync(Path.Combine(extractDir, "notadll.dll"), "this is not a valid assembly");

        try
        {
            var context = NewContext("Kraken.RunPackageAssembly", new Dictionary<string, string>
            {
                [PackageRunnerConfigKeys.AssemblyPath] = "notadll.dll",
            }, logs, extractDir);

            var success = await handler.HandleAsync(context, CancellationToken.None);

            success.Should().BeFalse();
            logs.Should().Contain(l => l.Level == "error");
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
            "the pack target must produce kraken.packagerunner-1.0.0.kdeploy-step");
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

        manifest.Id.Should().Be("kraken.packagerunner");
        manifest.Version.Should().Be("1.0.0");
        manifest.StepTypes.Should().HaveCount(2);
        manifest.StepTypes.Should().Contain("Kraken.RunPackageExecutable");
        manifest.StepTypes.Should().Contain("Kraken.RunPackageAssembly");
        manifest.ExecutorTypeName.Should().Be(typeof(PackageRunnerStepHandler).FullName!);
        manifest.ExecutorAssembly.Should().Be("KrakenDeploy.Steps.PackageRunner.dll");

        zip.GetEntry($"executor/{manifest.ExecutorAssembly}").Should().NotBeNull();
    }

    [Fact]
    public void Built_archive_bundles_Steps_Common_DLL()
    {
        var archivePath = FindBuiltArchive();
        archivePath.Should().NotBeNull();

        using var fs = File.OpenRead(archivePath!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        zip.GetEntry("executor/KrakenDeploy.Steps.Common.dll").Should().NotBeNull(
            "ScriptRunner lives in Steps.Common");
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
            Name: "PackageRunner",
            StepType: stepType,
            PackageId: "myapp",
            PackageVersion: "1.0.0",
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
            "steps", "KrakenDeploy.Steps.PackageRunner", "bin"));
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "kraken.packagerunner-1.0.0.kdeploy-step",
                SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }
}
