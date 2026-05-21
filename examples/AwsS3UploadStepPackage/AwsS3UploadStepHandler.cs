using KrakenDeploy.Contracts.Steps;

namespace AwsS3UploadStepPackage;

/// <summary>
/// Sample step handler: walks the deployment package on disk, picks files
/// matching <c>FileGlob</c>, and uploads each to S3.
/// <para>
/// What this handler demonstrates (the patterns Kraken expects from a real
/// step package — see <c>docs/step-packages.md</c>):
/// </para>
/// <list type="bullet">
///   <item><description>Async work — every upload is awaited; <see cref="HandleAsync"/> returns only when the batch is done.</description></item>
///   <item><description>Log streaming — progress lines flow through <see cref="StepHandlerContext.LogAsync"/> as files complete, so the deployment-log UI updates live instead of dumping at the end.</description></item>
///   <item><description>Artifacts directory — the handler writes a JSON manifest of uploaded keys into <see cref="StepHandlerContext.ArtifactsDir"/>, picked up by the executor as a deployment artifact.</description></item>
///   <item><description>Cancellation — <see cref="CancellationToken"/> is honored end-to-end so an aborted deployment doesn't leak a half-finished upload batch.</description></item>
///   <item><description>Validation — missing required keys fail loudly with a clear log line, not a NullReferenceException.</description></item>
///   <item><description>Failure surfacing — per-file failures either stop the batch (default) or accumulate (<c>ContinueOnError = True</c>); either way the result reflects reality.</description></item>
/// </list>
/// <para>
/// The default constructor news up <see cref="NotImplementedAwsS3Uploader"/>
/// — sample authors swap that for an <c>AWSSDK.S3</c>-backed
/// implementation before shipping (see <c>README.md</c>).
/// </para>
/// </summary>
public sealed class AwsS3UploadStepHandler : IStepHandler
{
    private readonly Func<S3UploadConfig, IS3Uploader> _uploaderFactory;

    /// <summary>
    /// Production constructor — the agent's loader calls this via reflection.
    /// Default factory news up <see cref="AwsSdkS3Uploader"/>, which talks
    /// to real S3 via <c>AWSSDK.S3</c>.
    /// </summary>
    public AwsS3UploadStepHandler()
        : this(static cfg => new AwsSdkS3Uploader(cfg)) { }

    /// <summary>
    /// Test seam — the test project uses <c>InternalsVisibleTo</c> to reach
    /// this ctor and supplies a fake uploader. The factory takes the parsed
    /// <see cref="S3UploadConfig"/> so the production impl can pick up
    /// credentials + region from one bundle.
    /// </summary>
    internal AwsS3UploadStepHandler(Func<S3UploadConfig, IS3Uploader> uploaderFactory)
    {
        _uploaderFactory = uploaderFactory;
    }

    public bool CanHandle(string stepType)
        => stepType.Equals("Kraken.Steps.AwsS3Upload", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The step uploads files from the deployment package payload, so the
    /// executor must extract the primary package to <see cref="StepHandlerContext.ExtractDir"/>
    /// before the handler runs.
    /// </summary>
    public bool RequiresPackage => true;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!TryParseConfig(context.Step.Config, out var config, out var validationError))
        {
            await context.LogAsync("error",
                $"S3 upload step config is invalid: {validationError}").ConfigureAwait(false);
            return false;
        }

        if (string.IsNullOrEmpty(context.ExtractDir) || !Directory.Exists(context.ExtractDir))
        {
            await context.LogAsync("error",
                "S3 upload step requires a package; ExtractDir was empty or missing.")
                .ConfigureAwait(false);
            return false;
        }

        var files = EnumerateMatchingFiles(context.ExtractDir, config.FileGlob);
        if (files.Count == 0)
        {
            await context.LogAsync("warning",
                $"No files matched glob '{config.FileGlob}' under {context.ExtractDir} — nothing to upload.")
                .ConfigureAwait(false);
            // Empty match isn't a failure — it just means the variant didn't ship.
            // Authors who want this to error out can wrap with a `ContinueOnError = False` glob assertion.
            return true;
        }

        await context.LogAsync("info",
            $"Uploading {files.Count} file(s) to s3://{config.BucketName} (region {config.Region}) " +
            $"under prefix '{config.ObjectKeyPrefix}' " +
            $"using {(config.HasExplicitCredentials ? "explicit credentials" : "the AWS default credential chain")}.")
            .ConfigureAwait(false);

        await using var uploader = _uploaderFactory(config);
        var uploaded = new List<UploadedObject>(files.Count);
        var anyFailed = false;

        foreach (var (relPath, absPath) in files)
        {
            ct.ThrowIfCancellationRequested();

            var objectKey = CombineKey(config.ObjectKeyPrefix, relPath);
            try
            {
                long bytes;
                await using (var fs = File.OpenRead(absPath))
                {
                    bytes = await uploader
                        .PutObjectAsync(config.BucketName, objectKey, fs, config.CannedAcl, ct)
                        .ConfigureAwait(false);
                }

                uploaded.Add(new UploadedObject(objectKey, bytes));
                await context.LogAsync("info",
                    $"Uploaded {relPath} → s3://{config.BucketName}/{objectKey} ({bytes} bytes).")
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Bubble cancellation — the executor treats this as an aborted deployment.
                throw;
            }
            catch (Exception ex)
            {
                anyFailed = true;
                await context.LogAsync(
                    config.ContinueOnError ? "warning" : "error",
                    $"Failed to upload {relPath}: {ex.Message}").ConfigureAwait(false);
                if (!config.ContinueOnError)
                {
                    await WriteArtifactManifestAsync(context, uploaded, partial: true, ct)
                        .ConfigureAwait(false);
                    return false;
                }
            }
        }

        await WriteArtifactManifestAsync(context, uploaded, partial: anyFailed, ct)
            .ConfigureAwait(false);

        if (anyFailed)
        {
            await context.LogAsync("warning",
                $"{uploaded.Count}/{files.Count} upload(s) succeeded; the rest were tolerated due to ContinueOnError = True.")
                .ConfigureAwait(false);
        }
        return !anyFailed || config.ContinueOnError;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static bool TryParseConfig(
        IReadOnlyDictionary<string, string> raw,
        out S3UploadConfig                  config,
        out string                          error)
    {
        var bucket = raw.GetValueOrDefault(OctopusS3ConfigKeys.BucketName);
        var region = raw.GetValueOrDefault(OctopusS3ConfigKeys.Region);
        var prefix = raw.GetValueOrDefault(OctopusS3ConfigKeys.ObjectKeyPrefix) ?? string.Empty;
        var glob   = raw.GetValueOrDefault(OctopusS3ConfigKeys.FileGlob)        ?? "**/*";

        if (string.IsNullOrWhiteSpace(bucket))
        {
            config = null!;
            error  = $"required key '{OctopusS3ConfigKeys.BucketName}' is missing or blank.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(region))
        {
            config = null!;
            error  = $"required key '{OctopusS3ConfigKeys.Region}' is missing or blank.";
            return false;
        }

        var accessKeyId     = NullIfBlank(raw.GetValueOrDefault(OctopusS3ConfigKeys.AccessKeyId));
        var secretAccessKey = NullIfBlank(raw.GetValueOrDefault(OctopusS3ConfigKeys.SecretAccessKey));
        if ((accessKeyId is null) != (secretAccessKey is null))
        {
            config = null!;
            error  = $"keys '{OctopusS3ConfigKeys.AccessKeyId}' and " +
                     $"'{OctopusS3ConfigKeys.SecretAccessKey}' must both be set or both be blank " +
                     "(blank = use the AWS SDK's default credential chain).";
            return false;
        }

        config = new S3UploadConfig
        {
            BucketName      = bucket,
            Region          = region,
            ObjectKeyPrefix = prefix,
            FileGlob        = glob,
            AccessKeyId     = accessKeyId,
            SecretAccessKey = secretAccessKey,
            CannedAcl       = raw.GetValueOrDefault(OctopusS3ConfigKeys.CannedAcl),
            ContinueOnError = ParseBool(raw.GetValueOrDefault(OctopusS3ConfigKeys.ContinueOnError)),
        };
        error = string.Empty;
        return true;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static List<(string relPath, string absPath)> EnumerateMatchingFiles(
        string root, string glob)
    {
        // Minimal glob support — `**/*` (everything), `*.ext`, `subdir/**`.
        // A real impl would lean on Microsoft.Extensions.FileSystemGlobbing
        // (no extra dep in Contracts, so left as an obvious extension point).
        var rootInfo = new DirectoryInfo(root);
        var pattern  = NormalizeGlob(glob, out var searchOption);

        return [.. rootInfo
            .EnumerateFiles(pattern, searchOption)
            .Select(f => (Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), f.FullName))
            .OrderBy(t => t.Item1, StringComparer.OrdinalIgnoreCase)];
    }

    private static string NormalizeGlob(string glob, out SearchOption opt)
    {
        if (string.IsNullOrWhiteSpace(glob) || glob == "**/*" || glob == "**")
        {
            opt = SearchOption.AllDirectories;
            return "*";
        }
        if (glob.StartsWith("**/", StringComparison.Ordinal))
        {
            opt = SearchOption.AllDirectories;
            return glob[3..];
        }
        opt = SearchOption.TopDirectoryOnly;
        return glob;
    }

    private static string CombineKey(string prefix, string relPath)
    {
        var trimmed = prefix.Trim('/');
        return string.IsNullOrEmpty(trimmed) ? relPath : $"{trimmed}/{relPath}";
    }

    private static bool ParseBool(string? value)
        => value is not null
        && (value.Equals("True", StringComparison.OrdinalIgnoreCase)
         || value.Equals("1",     StringComparison.OrdinalIgnoreCase));

    private static async Task WriteArtifactManifestAsync(
        StepHandlerContext context,
        IReadOnlyList<UploadedObject> uploaded,
        bool                          partial,
        CancellationToken             ct)
    {
        if (string.IsNullOrEmpty(context.ArtifactsDir))
        {
            return; // Executor didn't allocate an artifacts dir for this step.
        }

        Directory.CreateDirectory(context.ArtifactsDir);
        var path = Path.Combine(context.ArtifactsDir, "uploaded.json");

        var doc = System.Text.Json.JsonSerializer.Serialize(new
        {
            partial,
            count = uploaded.Count,
            objects = uploaded.Select(u => new { key = u.Key, bytes = u.Bytes }),
        }, ArtifactJsonOptions);

        await File.WriteAllTextAsync(path, doc, ct).ConfigureAwait(false);
    }

    private sealed record UploadedObject(string Key, long Bytes);

    private static readonly System.Text.Json.JsonSerializerOptions ArtifactJsonOptions = new()
    {
        WriteIndented = true,
    };
}
