using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.Kubernetes;

namespace KrakenDeploy.Steps.Kubernetes.Tests;

public sealed class KubernetesStepPackageTests
{
    private static readonly string[] AllStepTypes =
    [
        "Octopus.KubernetesDeployRawYaml",
        "Octopus.KubernetesDeployContainers",
        "Octopus.KubernetesDeployService",
        "Octopus.KubernetesDeployIngress",
        "Octopus.KubernetesDeployConfigMap",
        "Octopus.KubernetesDeploySecret",
        "Octopus.Kubernetes.Kustomize",
        "Octopus.HelmChartUpgrade",
        "Octopus.KubernetesRunScript",
    ];

    [Theory]
    [InlineData("Octopus.KubernetesDeployRawYaml", true)]
    [InlineData("octopus.kubernetesdeployrawyaml", true)]
    [InlineData("Octopus.KubernetesDeployContainers", true)]
    [InlineData("Octopus.KubernetesDeployService", true)]
    [InlineData("Octopus.KubernetesDeployIngress", true)]
    [InlineData("Octopus.KubernetesDeployConfigMap", true)]
    [InlineData("Octopus.KubernetesDeploySecret", true)]
    [InlineData("Octopus.Kubernetes.Kustomize", true)]
    [InlineData("Octopus.HelmChartUpgrade", true)]
    [InlineData("Octopus.KubernetesRunScript", true)]
    [InlineData("Kraken.Script", false)]
    [InlineData("Octopus.DockerRun", false)]
    [InlineData("", false)]
    public void CanHandle_returns_true_only_for_kubernetes_step_types(string stepType, bool expected)
    {
        var handler = new KubernetesStepHandler();
        handler.CanHandle(stepType).Should().Be(expected);
    }

    [Fact]
    public void Handler_does_not_require_a_package()
        => new KubernetesStepHandler().RequiresPackage.Should().BeFalse();

    [Fact]
    public void Handler_handles_all_nine_step_types()
    {
        var handler = new KubernetesStepHandler();
        foreach (var stepType in AllStepTypes)
        {
            handler.CanHandle(stepType).Should().BeTrue($"because {stepType} is a Kubernetes step");
        }
    }

    [Fact]
    public async Task HandleAsync_fails_when_no_cluster_connection_configured()
    {
        var handler = new KubernetesStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.KubernetesDeployRawYaml",
            new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("No Kubernetes connection"));
    }

    [Fact]
    public async Task HandleRawYaml_fails_when_no_yaml_provided()
    {
        var handler = new KubernetesStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.KubernetesDeployRawYaml",
            new Dictionary<string, string>
            {
                [KubernetesConfigKeys.KubeconfigPath] = "/tmp/fake-kubeconfig",
            }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("YAML"));
    }

    [Fact]
    public async Task HandleDeployContainers_fails_when_no_image()
    {
        var handler = new KubernetesStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.KubernetesDeployContainers",
            new Dictionary<string, string>
            {
                [KubernetesConfigKeys.KubeconfigPath] = "/tmp/fake-kubeconfig",
                [KubernetesConfigKeys.ResourceName] = "myapp",
            }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("Image"));
    }

    [Fact]
    public async Task HandleDeployService_fails_when_no_name()
    {
        var handler = new KubernetesStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.KubernetesDeployService",
            new Dictionary<string, string>
            {
                [KubernetesConfigKeys.KubeconfigPath] = "/tmp/fake-kubeconfig",
            }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("ResourceName"));
    }

    [Fact]
    public async Task HandleHelm_fails_when_no_release_name()
    {
        var handler = new KubernetesStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.HelmChartUpgrade",
            new Dictionary<string, string>
            {
                [KubernetesConfigKeys.KubeconfigPath] = "/tmp/fake-kubeconfig",
            }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("HelmReleaseName"));
    }

    [Fact]
    public async Task HandleRunScript_fails_when_no_script_body()
    {
        var handler = new KubernetesStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext("Octopus.KubernetesRunScript",
            new Dictionary<string, string>
            {
                [KubernetesConfigKeys.KubeconfigPath] = "/tmp/fake-kubeconfig",
            }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error" && l.Message.Contains("ScriptBody"));
    }

    [Fact]
    public void Built_archive_exists_at_the_expected_path()
    {
        FindBuiltArchive().Should().NotBeNull(
            "the pack target must produce octopus.kubernetes-1.0.0.kdeploy-step");
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

        manifest.Id.Should().Be("octopus.kubernetes");
        manifest.Version.Should().Be("1.0.0");
        manifest.StepTypes.Should().HaveCount(9);
        manifest.StepTypes.Should().Contain("Octopus.KubernetesDeployRawYaml");
        manifest.StepTypes.Should().Contain("Octopus.KubernetesDeployContainers");
        manifest.StepTypes.Should().Contain("Octopus.KubernetesDeployService");
        manifest.StepTypes.Should().Contain("Octopus.KubernetesDeployIngress");
        manifest.StepTypes.Should().Contain("Octopus.KubernetesDeployConfigMap");
        manifest.StepTypes.Should().Contain("Octopus.KubernetesDeploySecret");
        manifest.StepTypes.Should().Contain("Octopus.Kubernetes.Kustomize");
        manifest.StepTypes.Should().Contain("Octopus.HelmChartUpgrade");
        manifest.StepTypes.Should().Contain("Octopus.KubernetesRunScript");
        manifest.ExecutorTypeName.Should().Be(typeof(KubernetesStepHandler).FullName!);
        manifest.ExecutorAssembly.Should().Be("KrakenDeploy.Steps.Kubernetes.dll");

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
            "KubectlCliRunner lives in Steps.Common");
        zip.GetEntry("executor/Octostache.dll").Should().NotBeNull(
            "Octostache is used for variable resolution in YAML/scripts");
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
            Name: "Kubernetes",
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
            "steps", "KrakenDeploy.Steps.Kubernetes", "bin"));
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "octopus.kubernetes-1.0.0.kdeploy-step",
                SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }
}
