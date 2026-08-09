using System.Globalization;
using System.IO.Compression;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Steps.HealthCheck;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Steps.HealthCheck.Tests;

public sealed class HealthCheckStepPackageTests
    : IClassFixture<HealthCheckStepPackageTests.LocalStatusServer>
{
    /// <summary>
    /// Loopback stand-in for httpbin.org's <c>/status/{code}</c>. The probe
    /// tests used to hit the live service, which made them fail whenever
    /// httpbin.org itself was degraded (503s took CI down on 2026-08-09) —
    /// a health-check HANDLER test must not depend on a third party's health.
    /// </summary>
    public sealed class LocalStatusServer : IAsyncLifetime
    {
        private WebApplication? _app;

        public string BaseUrl { get; private set; } = "";
        public int Port { get; private set; }

        public async Task InitializeAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            _app = builder.Build();
            _app.MapGet("/", () => Results.Ok());
            _app.MapGet("/status/{code:int}", (int code) => Results.StatusCode(code));
            await _app.StartAsync();
            Port    = new Uri(_app.Urls.First()).Port;
            BaseUrl = $"http://127.0.0.1:{Port}";
        }

        public async Task DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }
    }

    private readonly LocalStatusServer _server;

    public HealthCheckStepPackageTests(LocalStatusServer server) => _server = server;

    [Theory]
    [InlineData("Octopus.HealthCheck", true)]
    [InlineData("octopus.healthcheck", true)]
    [InlineData("Kraken.Script", false)]
    [InlineData("", false)]
    public void CanHandle_returns_true_only_for_Octopus_HealthCheck(string stepType, bool expected)
    {
        var handler = new HealthCheckStepHandler();
        handler.CanHandle(stepType).Should().Be(expected);
    }

    [Fact]
    public void Handler_does_not_require_a_package()
        => new HealthCheckStepHandler().RequiresPackage.Should().BeFalse();

    [Fact]
    public async Task HandleAsync_fails_when_no_uri_or_host_configured()
    {
        var handler = new HealthCheckStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext(new Dictionary<string, string>(), logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Level == "error");
    }

    [Fact]
    public async Task HandleAsync_succeeds_on_http_200()
    {
        var handler = new HealthCheckStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext(new Dictionary<string, string>
        {
            [HealthCheckConfigKeys.Uri] = $"{_server.BaseUrl}/status/200",
            [HealthCheckConfigKeys.RetryAttempts] = "1",
            [HealthCheckConfigKeys.TimeoutSeconds] = "10",
        }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeTrue();
        logs.Should().Contain(l => l.Message.Contains("succeeded"));
    }

    [Fact]
    public async Task HandleAsync_fails_on_unexpected_status_code()
    {
        var handler = new HealthCheckStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext(new Dictionary<string, string>
        {
            [HealthCheckConfigKeys.Uri] = $"{_server.BaseUrl}/status/500",
            [HealthCheckConfigKeys.ExpectedStatusCode] = "200",
            [HealthCheckConfigKeys.RetryAttempts] = "1",
            [HealthCheckConfigKeys.TimeoutSeconds] = "10",
        }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeFalse();
        logs.Should().Contain(l => l.Message.Contains("expected status 200, got 500"));
    }

    [Fact]
    public async Task HandleAsync_warn_mode_continues_on_failure()
    {
        var handler = new HealthCheckStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext(new Dictionary<string, string>
        {
            [HealthCheckConfigKeys.Uri] = $"{_server.BaseUrl}/status/500",
            [HealthCheckConfigKeys.RetryAttempts] = "1",
            [HealthCheckConfigKeys.TimeoutSeconds] = "10",
            [HealthCheckConfigKeys.FailureAction] = "warn",
        }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeTrue();
        logs.Should().Contain(l => l.Level == "warning" && l.Message.Contains("FailureAction=warn"));
    }

    [Fact]
    public async Task HandleAsync_constructs_uri_from_host_and_protocol()
    {
        var handler = new HealthCheckStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext(new Dictionary<string, string>
        {
            [HealthCheckConfigKeys.Host] = "127.0.0.1",
            [HealthCheckConfigKeys.Protocol] = "http",
            [HealthCheckConfigKeys.Port] = _server.Port.ToString(CultureInfo.InvariantCulture),
            [HealthCheckConfigKeys.RetryAttempts] = "1",
            [HealthCheckConfigKeys.TimeoutSeconds] = "10",
        }, logs);

        var success = await handler.HandleAsync(context, CancellationToken.None);

        success.Should().BeTrue();
        logs.Should().Contain(l => l.Message.Contains($"http://127.0.0.1:{_server.Port}"));
    }

    [Fact]
    public async Task HandleAsync_retries_configured_number_of_times()
    {
        var handler = new HealthCheckStepHandler();
        var logs = new List<(string Level, string Message)>();
        var context = NewContext(new Dictionary<string, string>
        {
            [HealthCheckConfigKeys.Uri] = $"{_server.BaseUrl}/status/500",
            [HealthCheckConfigKeys.RetryAttempts] = "3",
            [HealthCheckConfigKeys.RetryDelaySeconds] = "0",
            [HealthCheckConfigKeys.TimeoutSeconds] = "10",
        }, logs);

        await handler.HandleAsync(context, CancellationToken.None);

        logs.Count(l => l.Level == "warning" && l.Message.Contains("attempt")).Should().Be(3);
    }

    [Fact]
    public void Built_archive_exists_at_the_expected_path()
    {
        FindBuiltArchive().Should().NotBeNull(
            "the pack target must produce octopus.healthcheck-<version>.kdeploy-step");
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

        manifest.Id.Should().Be("octopus.healthcheck");
        manifest.Version.Should().Be(ArchiveVersion(FindBuiltArchive()!),
            "the manifest version and the archive filename both come from "
            + "KrakenStepPackageVersion in the csproj and must agree");
        manifest.StepTypes.Should().ContainSingle().Which.Id.Should().Be("Octopus.HealthCheck");
        manifest.ExecutorTypeName.Should().Be(typeof(HealthCheckStepHandler).FullName!);
        manifest.ExecutorAssembly.Should().Be("KrakenDeploy.Steps.HealthCheck.dll");

        zip.GetEntry($"executor/{manifest.ExecutorAssembly}").Should().NotBeNull();
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
            Name: "Health Check",
            StepType: "Octopus.HealthCheck",
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
            "steps", "KrakenDeploy.Steps.HealthCheck", "bin"));
        return Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "octopus.healthcheck-*.kdeploy-step",
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
