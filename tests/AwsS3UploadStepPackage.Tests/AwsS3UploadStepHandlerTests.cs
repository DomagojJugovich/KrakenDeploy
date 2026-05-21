using AwsS3UploadStepPackage;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Steps;

namespace AwsS3UploadStepPackage.Tests;

/// <summary>
/// Sample-handler tests driven through a fake <see cref="IS3Uploader"/>.
/// Goal: verify the patterns docs/step-packages.md tells authors to follow —
/// async upload, log streaming, artifacts manifest, cancellation respect,
/// config-validation error path, ContinueOnError semantics.
/// </summary>
public sealed class AwsS3UploadStepHandlerTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"kraken-s3-sample-{Guid.NewGuid():N}");

    public AwsS3UploadStepHandlerTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void CanHandle_returns_true_only_for_the_step_type_it_owns()
    {
        var handler = new AwsS3UploadStepHandler();
        handler.CanHandle("Kraken.Steps.AwsS3Upload").Should().BeTrue();
        handler.CanHandle("kraken.steps.awss3upload").Should().BeTrue(
            "stepType matching is case-insensitive");
        handler.CanHandle("Octopus.Manual").Should().BeFalse();
    }

    [Fact]
    public async Task Missing_BucketName_fails_loudly_with_an_error_log()
    {
        var (handler, fake, ctx, logs) = StageHandler(config: new Dictionary<string, string>
        {
            // BucketName intentionally omitted
            ["Kraken.AwsS3.Region"] = "eu-central-1",
        });

        var ok = await handler.HandleAsync(ctx, CancellationToken.None);

        ok.Should().BeFalse();
        logs.Should().Contain(l => l.level == "error" && l.message.Contains("BucketName"));
        fake.UploadedKeys.Should().BeEmpty("validation must fail before any upload is attempted");
    }

    [Fact]
    public async Task No_files_matching_glob_is_a_warning_not_a_failure()
    {
        // Empty extract dir → glob matches nothing.
        var cfg = new Dictionary<string, string>(HappyConfig())
        {
            ["Kraken.AwsS3.FileGlob"] = "*.notpresent",
        };
        var (handler, fake, ctx, logs) = StageHandler(config: cfg);

        var ok = await handler.HandleAsync(ctx, CancellationToken.None);

        ok.Should().BeTrue("an empty match isn't a failure — the variant just didn't ship");
        logs.Should().Contain(l => l.level == "warning" && l.message.Contains("No files matched"));
        fake.UploadedKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task Happy_path_uploads_every_matching_file_and_writes_artifact_manifest()
    {
        StageFile("appsettings.json", "{}");
        StageFile("bin/app.exe",      "MZ-fake");
        StageFile("bin/app.dll",      "DLL-fake");

        var (handler, fake, ctx, logs) = StageHandler(config: HappyConfig());

        var ok = await handler.HandleAsync(ctx, CancellationToken.None);

        ok.Should().BeTrue();
        fake.UploadedKeys.Should().BeEquivalentTo([
            "releases/1.0.0/appsettings.json",
            "releases/1.0.0/bin/app.dll",
            "releases/1.0.0/bin/app.exe",
        ]);

        logs.Should().Contain(l => l.level == "info" && l.message.Contains("Uploaded appsettings.json"));
        logs.Should().Contain(l => l.level == "info" && l.message.Contains("Uploaded bin/app.exe"));

        // Artifact manifest landed on disk.
        var manifestPath = Path.Combine(ctx.ArtifactsDir, "uploaded.json");
        File.Exists(manifestPath).Should().BeTrue();
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        manifestJson.Should().Contain("\"count\": 3");
        manifestJson.Should().Contain("\"partial\": false");
        manifestJson.Should().Contain("releases/1.0.0/appsettings.json");
    }

    [Fact]
    public async Task Hard_failure_aborts_the_batch_when_ContinueOnError_is_false()
    {
        StageFile("good-1.txt", "ok");
        StageFile("bad-trigger.txt", "boom");
        StageFile("good-2.txt", "ok");

        var fake = new FakeS3Uploader();
        fake.FailWhenKeyContains = "bad-trigger";

        var (handler, _, ctx, logs) = StageHandler(
            config: HappyConfig(),
            uploaderOverride: fake);

        var ok = await handler.HandleAsync(ctx, CancellationToken.None);

        ok.Should().BeFalse();
        logs.Should().Contain(l => l.level == "error" && l.message.Contains("bad-trigger"));
        // Manifest was written even on partial failure (so the operator
        // can see which files actually landed).
        var manifestPath = Path.Combine(ctx.ArtifactsDir, "uploaded.json");
        File.Exists(manifestPath).Should().BeTrue();
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        manifestJson.Should().Contain("\"partial\": true");
    }

    [Fact]
    public async Task ContinueOnError_true_tolerates_per_file_failures_and_returns_true()
    {
        StageFile("good-1.txt", "ok");
        StageFile("bad-trigger.txt", "boom");
        StageFile("good-2.txt", "ok");

        var fake = new FakeS3Uploader();
        fake.FailWhenKeyContains = "bad-trigger";

        var continueConfig = new Dictionary<string, string>(HappyConfig())
        {
            ["Kraken.AwsS3.ContinueOnError"] = "True",
        };
        var (handler, _, ctx, logs) = StageHandler(
            config: continueConfig,
            uploaderOverride: fake);

        var ok = await handler.HandleAsync(ctx, CancellationToken.None);

        ok.Should().BeTrue("ContinueOnError = True turns failures into warnings, not aborts");
        fake.UploadedKeys.Should().BeEquivalentTo(["releases/1.0.0/good-1.txt", "releases/1.0.0/good-2.txt"]);
        logs.Should().Contain(l => l.level == "warning" && l.message.Contains("bad-trigger"));
    }

    [Fact]
    public async Task Cancellation_is_rethrown_so_the_executor_marks_the_deployment_aborted()
    {
        StageFile("a.txt", "a");
        StageFile("b.txt", "b");

        var fake = new FakeS3Uploader();
        using var cts = new CancellationTokenSource();
        fake.OnUploadStarted = (_, _) => cts.Cancel();

        var (handler, _, ctx, _) = StageHandler(config: HappyConfig(), uploaderOverride: fake);

        var act = async () => await handler.HandleAsync(ctx, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Asymmetric_credentials_fail_with_a_clear_error()
    {
        var cfg = new Dictionary<string, string>(HappyConfig())
        {
            ["Kraken.AwsS3.AccessKeyId"] = "AKIAEXAMPLE",
            // SecretAccessKey deliberately omitted
        };
        var (handler, _, ctx, logs) = StageHandler(config: cfg);

        var ok = await handler.HandleAsync(ctx, CancellationToken.None);

        ok.Should().BeFalse();
        logs.Should().Contain(l =>
            l.level == "error"
            && l.message.Contains("AccessKeyId")
            && l.message.Contains("SecretAccessKey"));
    }

    [Fact]
    public async Task Both_credentials_blank_is_explicitly_allowed_default_chain()
    {
        StageFile("a.txt", "a");
        var (handler, fake, ctx, logs) = StageHandler(config: HappyConfig());

        var ok = await handler.HandleAsync(ctx, CancellationToken.None);

        ok.Should().BeTrue();
        // The pre-upload announcement names which credential path we picked.
        logs.Should().Contain(l =>
            l.level == "info"
            && l.message.Contains("default credential chain"));
        fake.UploadedKeys.Should().HaveCount(1);
    }

    [Fact]
    public async Task Both_credentials_provided_announces_explicit_credentials()
    {
        StageFile("a.txt", "a");
        var cfg = new Dictionary<string, string>(HappyConfig())
        {
            ["Kraken.AwsS3.AccessKeyId"]     = "AKIAEXAMPLE",
            ["Kraken.AwsS3.SecretAccessKey"] = "an-example-secret-not-real",
        };
        var (handler, _, ctx, logs) = StageHandler(config: cfg);

        var ok = await handler.HandleAsync(ctx, CancellationToken.None);

        ok.Should().BeTrue();
        logs.Should().Contain(l =>
            l.level == "info"
            && l.message.Contains("explicit credentials"));
    }

    [Fact]
    public async Task Uploader_is_disposed_after_a_successful_batch()
    {
        StageFile("a.txt", "a");
        var (handler, fake, ctx, _) = StageHandler(config: HappyConfig());

        await handler.HandleAsync(ctx, CancellationToken.None);

        fake.Disposed.Should().BeTrue(
            "the handler must await using its uploader so AmazonS3Client gets disposed");
    }

    [Fact]
    public async Task Missing_extract_dir_fails_with_a_clear_error()
    {
        var (handler, _, ctx, logs) = StageHandler(
            config: HappyConfig(),
            extractDirOverride: Path.Combine(_workspace, "no-such-extract"));

        var ok = await handler.HandleAsync(ctx, CancellationToken.None);

        ok.Should().BeFalse();
        logs.Should().Contain(l => l.level == "error" && l.message.Contains("ExtractDir"));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private (AwsS3UploadStepHandler handler,
             FakeS3Uploader         fake,
             StepHandlerContext     context,
             List<(string level, string message)> logs)
        StageHandler(
            IReadOnlyDictionary<string, string> config,
            IS3Uploader?                        uploaderOverride = null,
            string?                             extractDirOverride = null)
    {
        var extractDir   = extractDirOverride ?? Path.Combine(_workspace, "extract");
        var artifactsDir = Path.Combine(_workspace, "artifacts");
        Directory.CreateDirectory(artifactsDir);
        // Don't create the extract dir if the caller is asking us to test
        // the missing-dir failure path.
        if (extractDirOverride is null)
        {
            Directory.CreateDirectory(extractDir);
        }

        var fake    = uploaderOverride as FakeS3Uploader ?? new FakeS3Uploader();
        var handler = new AwsS3UploadStepHandler(_cfg => uploaderOverride ?? fake);

        var logs = new List<(string level, string message)>();
        var ctx  = new StepHandlerContext
        {
            Plan = new DeploymentPlan(
                DeploymentId: Guid.NewGuid(),
                EnvironmentName: "test",
                Steps: [],
                Variables: new Dictionary<string, string>(),
                ArrayVariables: new Dictionary<string, string[]>()),
            Step = new DeploymentStepPlan(
                Index: 0,
                Name: "Upload to S3",
                StepType: "Kraken.Steps.AwsS3Upload",
                PackageId: "kraken.sample.app",
                PackageVersion: "1.0.0",
                Config: config),
            ExtractDir   = extractDir,
            ArtifactsDir = artifactsDir,
            LogAsync     = (level, message) =>
            {
                logs.Add((level, message));
                return Task.CompletedTask;
            },
        };
        return (handler, fake, ctx, logs);
    }

    private static Dictionary<string, string> HappyConfig() => new()
    {
        ["Kraken.AwsS3.BucketName"]      = "kraken-test-bucket",
        ["Kraken.AwsS3.Region"]          = "eu-central-1",
        ["Kraken.AwsS3.ObjectKeyPrefix"] = "releases/1.0.0",
        ["Kraken.AwsS3.FileGlob"]        = "**/*",
    };

    private void StageFile(string relPath, string content)
    {
        var extractDir = Path.Combine(_workspace, "extract");
        Directory.CreateDirectory(extractDir);
        var full = Path.Combine(extractDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>
    /// In-memory fake. Lets tests force a per-key failure and observe order
    /// of uploads. Public-ish (same assembly) so the test helper can wire it
    /// via the internal handler ctor.
    /// </summary>
    private sealed class FakeS3Uploader : IS3Uploader
    {
        public List<string>                    UploadedKeys        { get; } = [];
        public string?                         FailWhenKeyContains { get; set; }
        public Action<string, string>?         OnUploadStarted     { get; set; }
        public bool                            Disposed            { get; private set; }

        public Task<long> PutObjectAsync(
            string bucket, string objectKey, Stream content,
            string? cannedAcl, CancellationToken ct)
        {
            OnUploadStarted?.Invoke(bucket, objectKey);
            ct.ThrowIfCancellationRequested();

            if (FailWhenKeyContains is not null
                && objectKey.Contains(FailWhenKeyContains, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"FakeS3Uploader was instructed to fail on '{objectKey}'.");
            }
            UploadedKeys.Add(objectKey);
            return Task.FromResult(content.Length);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
