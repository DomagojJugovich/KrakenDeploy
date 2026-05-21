# AWS S3 Upload — sample step package

Reference step-package showing how to ship a non-trivial `IStepHandler`
for KrakenDeploy. Pair with `docs/step-packages.md` (root authoring
guide) — this README only covers what's specific to this example.

## What this package does

Step type: `Kraken.Steps.AwsS3Upload`

Given a primary deployment package extracted by the agent, it walks the
extracted tree, picks files matching a configured glob, and uploads each
to an S3 bucket. A JSON manifest of uploaded objects is written into the
step's artifacts directory.

## What it demonstrates

| Pattern | Where to look |
|---|---|
| Async I/O end-to-end | `AwsS3UploadStepHandler.HandleAsync` — every upload is awaited. |
| Live log streaming | `context.LogAsync("info", …)` on each completed file. |
| Artifacts directory | `WriteArtifactManifestAsync` drops `uploaded.json` into `context.ArtifactsDir`. |
| Cancellation honored | `ct.ThrowIfCancellationRequested()` between files; `OperationCanceledException` rethrown verbatim. |
| Config validation | `TryParseConfig` fails loudly with a clear log line for missing required keys. |
| Test seam without DI | `internal` ctor taking a `Func<string, IS3Uploader>` factory + `[InternalsVisibleTo]`. |
| Sensitive variables | UI schema marks `SecretAccessKey` as `sensitive: true` and uses the `variable` widget. |

## Wiring a real S3 client

The default `IS3Uploader` implementation throws — replace it before
shipping. Pattern (using `AWSSDK.S3`):

```csharp
internal sealed class AwsSdkS3Uploader : IS3Uploader
{
    private readonly Amazon.S3.AmazonS3Client _client;

    public AwsSdkS3Uploader(string region)
    {
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        _client      = new Amazon.S3.AmazonS3Client(endpoint);
    }

    public async Task<long> PutObjectAsync(
        string bucket, string objectKey, Stream content,
        string? cannedAcl, CancellationToken ct)
    {
        var req = new Amazon.S3.Model.PutObjectRequest
        {
            BucketName  = bucket,
            Key         = objectKey,
            InputStream = content,
            AutoCloseStream = false,
        };
        if (!string.IsNullOrWhiteSpace(cannedAcl))
        {
            req.CannedACL = new Amazon.S3.S3CannedACL(cannedAcl);
        }
        await _client.PutObjectAsync(req, ct).ConfigureAwait(false);
        return content.Length;
    }
}
```

Then change the production ctor in `AwsS3UploadStepHandler.cs`:

```csharp
public AwsS3UploadStepHandler()
    : this(region => new AwsSdkS3Uploader(region)) { }
```

Add `AWSSDK.S3` to your CSPROJ and you're done. Credentials are picked
up from the AWS SDK's normal chain (env vars, IAM role, shared profile)
— the handler doesn't fish them out of the step config directly. If you
need to override that, hand the credentials to the `AmazonS3Client`
constructor via a `Amazon.Runtime.BasicAWSCredentials` you build from
the variable-resolved `Kraken.AwsS3.AccessKeyId` + `SecretAccessKey`.

## Build + sign

```bash
# Build the .kdeploy-step (lands in bin/Release/net10.0/)
dotnet build examples/AwsS3UploadStepPackage -c Release

# Sign with your project key — replace the dev sentinel with a real RSA-SHA256 signature
kraken pack examples/AwsS3UploadStepPackage/AwsS3UploadStepPackage.csproj \
            --key ./kraken-signing.key
```

## Upload to a Kraken server (local test)

```bash
curl -X POST https://kraken.local/api/step-packages \
     -H "X-Api-Key: $KRAKEN_API_KEY" \
     -F "file=@bin/Release/net10.0/kraken.steps.aws-s3-upload-1.0.0.kdeploy-step"
```

## Tests

The `AwsS3UploadStepPackage.Tests` project drives the handler with a
fake `IS3Uploader` so no AWS creds are needed:

```bash
dotnet test tests/AwsS3UploadStepPackage.Tests
```

Covers: missing config, no-match glob, happy-path upload + artifact
manifest, partial failure with ContinueOnError, hard failure without
ContinueOnError, cancellation.
