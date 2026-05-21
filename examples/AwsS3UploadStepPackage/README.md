# AWS S3 Upload — sample step package

Reference step package showing how to ship a non-trivial `IStepHandler`
for KrakenDeploy. **This one actually works against real S3** —
it bundles `AWSSDK.S3` v4 and ships the `Amazon.S3.AmazonS3Client`
runtime DLLs inside the `.kdeploy-step` archive. Pair with the root
authoring guide at `docs/step-packages.md`.

## What this package does

Step type: `Kraken.Steps.AwsS3Upload`

Given a primary deployment package extracted by the agent, it walks the
extracted tree, picks files matching a configured glob, and uploads each
to an S3 bucket. A JSON manifest of the uploaded objects is written into
the step's artifacts directory.

## What it demonstrates

| Pattern | Where to look |
|---|---|
| Async I/O end-to-end | `AwsS3UploadStepHandler.HandleAsync` — every upload is awaited. |
| Live log streaming | `context.LogAsync("info", …)` after every completed file. |
| Artifacts directory | `WriteArtifactManifestAsync` drops `uploaded.json` into `context.ArtifactsDir`. |
| Cancellation honored | `ct.ThrowIfCancellationRequested()` between files; `OperationCanceledException` rethrown unchanged. |
| Config validation | `TryParseConfig` fails loudly with a clear log line for missing required keys and asymmetric credentials. |
| Test seam without DI | `internal` ctor taking `Func<S3UploadConfig, IS3Uploader>` + `[InternalsVisibleTo]`. |
| Sensitive variables | UI schema marks `SecretAccessKey` as `sensitive: true` + `widget: "variable"`. |
| Real third-party SDK packaged correctly | `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` puts `AWSSDK.S3.dll` + `AWSSDK.Core.dll` into the archive so the agent's loader resolves them at runtime. |
| Disposable resources | `IS3Uploader : IAsyncDisposable` + `await using` in the handler — `AmazonS3Client.Dispose()` runs at end-of-step. |

## Credentials

Both `Kraken.AwsS3.AccessKeyId` + `Kraken.AwsS3.SecretAccessKey` set:
the handler builds a `BasicAWSCredentials` with those values. Typical
when the deployment runs from an on-prem agent — bind them to sensitive
Kraken variables, never hand-type them.

Both blank: the handler falls back to the AWS SDK's default credential
chain — env vars (`AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY`), then
shared credentials file, then the instance metadata service (EC2 / ECS /
EKS IAM role). Right choice for agents running on AWS infrastructure
with an attached IAM role.

Only one of the two set: the handler refuses with a clear error log
line — fail-fast, no surprise default fallback.

## Build + sign

```bash
# Build the .kdeploy-step (lands in bin/Release/net10.0/)
dotnet build examples/AwsS3UploadStepPackage -c Release

# Sign with your project key — replaces the dev sentinel signature
# with a real RSA-SHA256 signature
kraken pack examples/AwsS3UploadStepPackage/AwsS3UploadStepPackage.csproj \
            --key ./kraken-signing.key
```

The produced archive carries:

```
manifest.json
ui/ui-schema.json
executor/
    AwsS3UploadStepPackage.dll
    AWSSDK.S3.dll
    AWSSDK.Core.dll
    (transitive deps the agent doesn't already host)
```

## Upload to a Kraken server (local test)

```bash
curl -X POST https://kraken.local/api/step-packages \
     -H "X-Api-Key: $KRAKEN_API_KEY" \
     -F "file=@bin/Release/net10.0/kraken.steps.aws-s3-upload-1.0.0.kdeploy-step"
```

## Tests

The `AwsS3UploadStepPackage.Tests` project drives the handler with a
fake `IS3Uploader` so no AWS credentials are needed:

```bash
dotnet test tests/AwsS3UploadStepPackage.Tests
```

Coverage:

- Step-type discrimination (`CanHandle`)
- Missing required config keys fail loudly
- Asymmetric credentials (only one of access-key / secret) rejected
- Both-blank credentials announce "default credential chain" in the log
- Both-populated credentials announce "explicit credentials" in the log
- Uploader disposed after a successful batch
- No-match glob is a warning, not a failure
- Happy-path upload + artifact manifest contents
- Hard failure aborts the batch when `ContinueOnError = False`
- `ContinueOnError = True` tolerates per-file failures
- Cancellation rethrown as `OperationCanceledException`
- Missing `ExtractDir` fails with a clear error
- `AwsSdkS3Uploader.ResolveCredentials` — `BasicAWSCredentials` vs
  default-chain vs asymmetric-throws

## Pointers for your own AWS step

Want to add `kraken.steps.aws-cloudfront-invalidate` or
`kraken.steps.aws-secretsmanager-fetch`? Copy this project as a
starting point — the credential plumbing, the test seam, the
`<CopyLocalLockFileAssemblies>` knob, and the schema layout transfer
directly. Swap `AWSSDK.S3` for `AWSSDK.CloudFront` /
`AWSSDK.SecretsManager` in the csproj and replace `AwsSdkS3Uploader`
with your own thin wrapper.
