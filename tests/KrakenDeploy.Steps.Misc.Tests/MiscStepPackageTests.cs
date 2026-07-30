using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Misc;

namespace KrakenDeploy.Steps.Misc.Tests;

public sealed class MiscStepPackageTests
{
    private static readonly string[] AllStepTypes =
    [
        "Octopus.Email",
        "Octopus.Nginx",
        "Octopus.Certificate.Import",
        "Octopus.Vhd",
    ];

    [Theory]
    [InlineData("Octopus.Email", true)]
    [InlineData("octopus.email", true)]
    [InlineData("Octopus.Nginx", true)]
    [InlineData("Octopus.Certificate.Import", true)]
    [InlineData("Octopus.Vhd", true)]
    [InlineData("Kraken.Script", false)]
    [InlineData("Octopus.DockerRun", false)]
    [InlineData("", false)]
    public void CanHandle_returns_true_only_for_misc_step_types(string stepType, bool expected)
    {
        var handler = new MiscStepHandler();
        handler.CanHandle(stepType).Should().Be(expected);
    }

    [Fact]
    public void Handler_does_not_require_a_package()
        => new MiscStepHandler().RequiresPackage.Should().BeFalse();

    [Fact]
    public void Handler_handles_all_four_step_types()
    {
        var handler = new MiscStepHandler();
        foreach (var stepType in AllStepTypes)
        {
            handler.CanHandle(stepType).Should().BeTrue($"because {stepType} is a Misc step");
        }
    }

    [Fact]
    public async Task HandleEmail_fails_when_no_smtp_host()
    {
        var handler = new MiscStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.Email", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("SmtpHost"));
    }

    [Fact]
    public async Task HandleEmail_fails_when_no_recipients()
    {
        var handler = new MiscStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.Email", new Dictionary<string, string>
        {
            [MiscConfigKeys.SmtpHost] = "smtp.example.com",
        }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("Email.To"));
    }

    [Fact]
    public async Task HandleCertificateImport_fails_when_no_cert_found()
    {
        var handler = new MiscStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.Certificate.Import", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("Certificate"));
    }

    [Fact]
    public async Task HandleVhd_fails_when_no_source()
    {
        var handler = new MiscStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.Vhd", new Dictionary<string, string>
        {
            [MiscConfigKeys.VhdDestinationPath] = "C:\\vhd\\dest.vhd",
        }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("source"));
    }

    [Fact]
    public async Task HandleVhd_fails_when_no_destination()
    {
        var handler = new MiscStepHandler();
        var logs = new List<(string Level, string Message)>();

        var extractDir = Path.Combine(Path.GetTempPath(), $"kraken-vhd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        await File.WriteAllTextAsync(Path.Combine(extractDir, "disk.vhd"), "fake-vhd");

        try
        {
            var context = NewContext("Octopus.Vhd", new Dictionary<string, string>(), logs, extractDir);

            var success = await handler.HandleAsync(context, CancellationToken.None);

            success.Should().BeFalse();
            logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("DestinationPath"));
        }
        finally
        {
            Directory.Delete(extractDir, true);
        }
    }

    [Fact]
    public async Task HandleVhd_copies_file_successfully()
    {
        var handler = new MiscStepHandler();
        var logs = new List<(string Level, string Message)>();

        var extractDir = Path.Combine(Path.GetTempPath(), $"kraken-vhd-{Guid.NewGuid():N}");
        var destPath = Path.Combine(Path.GetTempPath(), $"kraken-vhd-dest-{Guid.NewGuid():N}.vhd");
        Directory.CreateDirectory(extractDir);
        await File.WriteAllTextAsync(Path.Combine(extractDir, "disk.vhd"), "fake-vhd-content");

        try
        {
            var context = NewContext("Octopus.Vhd", new Dictionary<string, string>
            {
                [MiscConfigKeys.VhdDestinationPath] = destPath,
            }, logs, extractDir);

            var success = await handler.HandleAsync(context, CancellationToken.None);

            success.Should().BeTrue();
            File.Exists(destPath).Should().BeTrue();
            logs.Should().Contain(l => l.Message.Contains("copied successfully"));
        }
        finally
        {
            Directory.Delete(extractDir, true);
            if (File.Exists(destPath))
            {
                File.Delete(destPath);
            }
        }
    }

    [Fact]
    public void Built_archive_exists_at_the_expected_path()
    {
        FindBuiltArchive().Should().NotBeNull(
            "the pack target must produce octopus.misc-1.0.0.kdeploy-step");
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

        manifest.Id.Should().Be("octopus.misc");
        manifest.Version.Should().Be("1.0.0");
        manifest.StepTypes.Should().HaveCount(4);
        manifest.StepTypes.Should().Contain("Octopus.Email");
        manifest.StepTypes.Should().Contain("Octopus.Nginx");
        manifest.StepTypes.Should().Contain("Octopus.Certificate.Import");
        manifest.StepTypes.Should().Contain("Octopus.Vhd");
        manifest.ExecutorTypeName.Should().Be(typeof(MiscStepHandler).FullName!);
        manifest.ExecutorAssembly.Should().Be("KrakenDeploy.Steps.Misc.dll");

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
            "ScriptRunner lives in Steps.Common");
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
            Name: "Misc",
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
            "steps", "KrakenDeploy.Steps.Misc", "bin"));
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "octopus.misc-1.0.0.kdeploy-step",
                SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }
}
