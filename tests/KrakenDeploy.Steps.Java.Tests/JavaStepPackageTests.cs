using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Java;

namespace KrakenDeploy.Steps.Java.Tests;

public sealed class JavaStepPackageTests
{
    private static readonly string[] AllStepTypes =
    [
        "Octopus.JavaArchive",
        "Octopus.TomcatDeploy",
        "Octopus.TomcatState",
        "Octopus.TomcatDeployCertificate",
        "Octopus.WildFlyDeploy",
        "Octopus.WildFlyState",
        "Octopus.WildFlyCertificateDeploy",
        "Octopus.JavaDeployCertificate",
    ];

    [Theory]
    [InlineData("Octopus.JavaArchive", true)]
    [InlineData("octopus.javaarchive", true)]
    [InlineData("Octopus.TomcatDeploy", true)]
    [InlineData("Octopus.TomcatState", true)]
    [InlineData("Octopus.TomcatDeployCertificate", true)]
    [InlineData("Octopus.WildFlyDeploy", true)]
    [InlineData("Octopus.WildFlyState", true)]
    [InlineData("Octopus.WildFlyCertificateDeploy", true)]
    [InlineData("Octopus.JavaDeployCertificate", true)]
    [InlineData("Kraken.Script", false)]
    [InlineData("Octopus.DockerRun", false)]
    [InlineData("", false)]
    public void CanHandle_returns_true_only_for_java_step_types(string stepType, bool expected)
    {
        var handler = new JavaStepHandler();
        handler.CanHandle(stepType).Should().Be(expected);
    }

    [Fact]
    public void Handler_does_not_require_a_package()
        => new JavaStepHandler().RequiresPackage.Should().BeFalse();

    [Fact]
    public void Handler_handles_all_eight_step_types()
    {
        var handler = new JavaStepHandler();
        foreach (var stepType in AllStepTypes)
        {
            handler.CanHandle(stepType).Should().BeTrue($"because {stepType} is a Java step");
        }
    }

    [Fact]
    public async Task HandleJavaArchive_fails_when_no_deploy_path()
    {
        var handler = new JavaStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.JavaArchive", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("DeployPath"));
    }

    [Fact]
    public async Task HandleJavaArchive_fails_when_no_extract_dir()
    {
        var handler = new JavaStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.JavaArchive", new Dictionary<string, string>
        {
            [JavaConfigKeys.DeployPath] = "/opt/apps",
        }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("package"));
    }

    [Fact]
    public async Task HandleJavaArchive_deploys_war_files()
    {
        var handler = new JavaStepHandler();
        var logs = new List<(string Level, string Message)>();

        var extractDir = Path.Combine(Path.GetTempPath(), $"kraken-java-{Guid.NewGuid():N}");
        var deployDir = Path.Combine(Path.GetTempPath(), $"kraken-deploy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        await File.WriteAllTextAsync(Path.Combine(extractDir, "app.war"), "fake-war");
        await File.WriteAllTextAsync(Path.Combine(extractDir, "readme.txt"), "ignore");

        try
        {
            var context = NewContext("Octopus.JavaArchive", new Dictionary<string, string>
            {
                [JavaConfigKeys.DeployPath] = deployDir,
            }, logs, extractDir);

            var success = await handler.HandleAsync(context, CancellationToken.None);

            success.Should().BeTrue();
            File.Exists(Path.Combine(deployDir, "app.war")).Should().BeTrue();
            File.Exists(Path.Combine(deployDir, "readme.txt")).Should().BeFalse();
            logs.Should().Contain(l => l.Message.Contains("Deployed 1 archive(s)"));
        }
        finally
        {
            Directory.Delete(extractDir, true);
            if (Directory.Exists(deployDir))
            {
                Directory.Delete(deployDir, true);
            }
        }
    }

    [Fact]
    public async Task HandleTomcatDeploy_fails_when_no_tomcat_home_or_deploy_path()
    {
        var handler = new JavaStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.TomcatDeploy", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("TomcatHome"));
    }

    [Fact]
    public async Task HandleTomcatState_fails_when_no_home_or_service()
    {
        var handler = new JavaStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.TomcatState", new Dictionary<string, string>
        {
            [JavaConfigKeys.TomcatAction] = "restart",
        }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("TomcatHome"));
    }

    [Fact]
    public async Task HandleTomcatCertificate_fails_when_no_keystore_path()
    {
        var handler = new JavaStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.TomcatDeployCertificate", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("KeystorePath"));
    }

    [Fact]
    public async Task HandleWildFlyDeploy_fails_when_no_extract_dir()
    {
        var handler = new JavaStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.WildFlyDeploy", new Dictionary<string, string>
        {
            [JavaConfigKeys.WildFlyHome] = "/opt/wildfly",
        }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("package"));
    }

    [Fact]
    public async Task HandleWildFlyCertificate_fails_when_no_keystore_path()
    {
        var handler = new JavaStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.WildFlyCertificateDeploy", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("KeystorePath"));
    }

    [Fact]
    public async Task HandleJavaDeployCertificate_fails_when_no_keystore_path()
    {
        var handler = new JavaStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.JavaDeployCertificate", new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("KeystorePath"));
    }

    [Fact]
    public void Built_archive_exists_at_the_expected_path()
    {
        FindBuiltArchive().Should().NotBeNull(
            "the pack target must produce octopus.java-<version>.kdeploy-step");
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

        manifest.Id.Should().Be("octopus.java");
        manifest.Version.Should().Be(ArchiveVersion(FindBuiltArchive()!),
            "the manifest version and the archive filename both come from "
            + "KrakenStepPackageVersion in the csproj and must agree");
        manifest.StepTypes.Should().HaveCount(8);
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.JavaArchive");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.TomcatDeploy");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.TomcatState");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.TomcatDeployCertificate");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.WildFlyDeploy");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.WildFlyState");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.WildFlyCertificateDeploy");
        manifest.StepTypes.Should().Contain(t => t.Id == "Octopus.JavaDeployCertificate");
        manifest.ExecutorTypeName.Should().Be(typeof(JavaStepHandler).FullName!);
        manifest.ExecutorAssembly.Should().Be("KrakenDeploy.Steps.Java.dll");

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
            Name: "Java",
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
            "steps", "KrakenDeploy.Steps.Java", "bin"));
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "octopus.java-*.kdeploy-step",
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
