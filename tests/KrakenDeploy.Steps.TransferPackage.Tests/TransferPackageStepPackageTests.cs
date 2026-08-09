using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.TransferPackage;

namespace KrakenDeploy.Steps.TransferPackage.Tests;

public sealed class TransferPackageStepPackageTests
{
    [Theory]
    [InlineData("Octopus.TransferPackage", true)]
    [InlineData("octopus.transferpackage", true)]
    [InlineData("Kraken.Script", false)]
    [InlineData("", false)]
    public void CanHandle_returns_true_only_for_Octopus_TransferPackage(string stepType, bool expected)
    {
        var handler = new TransferPackageStepHandler();
        handler.CanHandle(stepType).Should().Be(expected);
    }

    [Fact]
    public void Handler_requires_a_package()
        => new TransferPackageStepHandler().RequiresPackage.Should().BeTrue();

    [Fact]
    public async Task HandleAsync_copies_files_to_destination_directory()
    {
        var handler = new TransferPackageStepHandler();
        var logs = new List<(string Level, string Message)>();

        var extractDir = Path.Combine(Path.GetTempPath(), $"kraken-test-{Guid.NewGuid():N}");
        var destDir = Path.Combine(Path.GetTempPath(), $"kraken-dest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        Directory.CreateDirectory(Path.Combine(extractDir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(extractDir, "app.dll"), "binary");
        await File.WriteAllTextAsync(Path.Combine(extractDir, "sub", "config.json"), "{}");

        try
        {
            var context = NewContext(new Dictionary<string, string>
            {
                [TransferPackageConfigKeys.DestinationType] = "file",
                [TransferPackageConfigKeys.DestinationPath] = destDir,
            }, logs, extractDir);

            var success = await handler.HandleAsync(context, CancellationToken.None);

            success.Should().BeTrue();
            File.Exists(Path.Combine(destDir, "app.dll")).Should().BeTrue();
            File.Exists(Path.Combine(destDir, "sub", "config.json")).Should().BeTrue();
            logs.Should().Contain(l => l.Message.Contains("Transferred 2 file(s)"));
        }
        finally
        {
            Directory.Delete(extractDir, true);
            if (Directory.Exists(destDir))
            {
                Directory.Delete(destDir, true);
            }
        }
    }

    [Fact]
    public async Task HandleAsync_fails_when_no_destination_path()
    {
        var handler = new TransferPackageStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext(new Dictionary<string, string>
        {
            [TransferPackageConfigKeys.DestinationType] = "file",
        }, logs, Path.GetTempPath());

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("DestinationPath"));
    }

    [Fact]
    public async Task HandleAsync_fails_when_no_destination_url_for_http()
    {
        var handler = new TransferPackageStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext(new Dictionary<string, string>
        {
            [TransferPackageConfigKeys.DestinationType] = "http",
        }, logs, Path.GetTempPath());

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("DestinationUrl"));
    }

    [Fact]
    public async Task HandleAsync_respects_file_pattern_filter()
    {
        var handler = new TransferPackageStepHandler();
        var logs = new List<(string Level, string Message)>();

        var extractDir = Path.Combine(Path.GetTempPath(), $"kraken-test-{Guid.NewGuid():N}");
        var destDir = Path.Combine(Path.GetTempPath(), $"kraken-dest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        await File.WriteAllTextAsync(Path.Combine(extractDir, "app.dll"), "binary");
        await File.WriteAllTextAsync(Path.Combine(extractDir, "readme.txt"), "text");

        try
        {
            var context = NewContext(new Dictionary<string, string>
            {
                [TransferPackageConfigKeys.DestinationType] = "file",
                [TransferPackageConfigKeys.DestinationPath] = destDir,
                [TransferPackageConfigKeys.FileNamePattern] = "*.dll",
            }, logs, extractDir);

            var success = await handler.HandleAsync(context, CancellationToken.None);

            success.Should().BeTrue();
            File.Exists(Path.Combine(destDir, "app.dll")).Should().BeTrue();
            File.Exists(Path.Combine(destDir, "readme.txt")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(extractDir, true);
            if (Directory.Exists(destDir))
            {
                Directory.Delete(destDir, true);
            }
        }
    }

    [Fact]
    public void Built_archive_exists_at_the_expected_path()
    {
        FindBuiltArchive().Should().NotBeNull(
            "the pack target must produce octopus.transferpackage-<version>.kdeploy-step");
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

        manifest.Id.Should().Be("octopus.transferpackage");
        manifest.Version.Should().Be(ArchiveVersion(FindBuiltArchive()!),
            "the manifest version and the archive filename both come from "
            + "KrakenStepPackageVersion in the csproj and must agree");
        manifest.StepTypes.Should().ContainSingle().Which.Id.Should().Be("Octopus.TransferPackage");
        manifest.ExecutorTypeName.Should().Be(typeof(TransferPackageStepHandler).FullName!);
        manifest.ExecutorAssembly.Should().Be("KrakenDeploy.Steps.TransferPackage.dll");

        zip.GetEntry($"executor/{manifest.ExecutorAssembly}").Should().NotBeNull();
    }

    private static StepHandlerContext NewContext(
        Dictionary<string, string> config,
        List<(string Level, string Message)> logs,
        string extractDir)
    {
        var plan = new DeploymentPlan(
            DeploymentId: Guid.NewGuid(),
            EnvironmentName: "Production",
            Steps: [],
            Variables: new Dictionary<string, string>(),
            ArrayVariables: new Dictionary<string, string[]>());

        var step = new DeploymentStepPlan(
            Index: 0,
            Name: "Transfer",
            StepType: "Octopus.TransferPackage",
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
            "steps", "KrakenDeploy.Steps.TransferPackage", "bin"));
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "octopus.transferpackage-*.kdeploy-step",
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
