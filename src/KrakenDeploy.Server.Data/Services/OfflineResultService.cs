using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KrakenDeploy.Contracts.Offline;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.ArtifactStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Ingests an offline result bundle — a zip returned from an offline-drop deployment
/// containing <c>deployment-result.json</c>, <c>deployment-log.txt</c>,
/// <c>artifacts/</c>, and an optional <c>signature.bin</c>.
/// <para>
/// Validates the HMAC if a key is configured, parses the result status, appends
/// log entries, stores artifacts, and transitions the deployment from
/// <see cref="DeploymentStatus.PendingOfflineResult"/> to the reported status.
/// </para>
/// </summary>
public class OfflineResultService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IArtifactStore artifactStore,
    IEncryptionService encryption,
    ILogger<OfflineResultService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Ingests the result bundle for the specified deployment.
    /// </summary>
    /// <param name="deploymentId">ID of the deployment that was executed offline.</param>
    /// <param name="resultBundle">The uploaded result bundle zip stream.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated deployment.</returns>
    public async Task<Deployment> IngestAsync(
        Guid deploymentId,
        Stream resultBundle,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var deployment = await db.Deployments
            .Include(d => d.Target)
            .Include(d => d.LogEntries)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Deployment {deploymentId} not found.");

        if (deployment.Status != DeploymentStatus.PendingOfflineResult)
        {
            throw new InvalidOperationException(
                $"Deployment {deploymentId} is in status '{deployment.Status}', " +
                "not 'PendingOfflineResult'. Only pending offline deployments accept result bundles.");
        }

        // Copy to a seekable memory stream so ZipArchive can read it
        using var ms = new MemoryStream();
        await resultBundle.CopyToAsync(ms, ct).ConfigureAwait(false);
        ms.Position = 0;

        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        // ── Validate HMAC signature ─────────────────────────────────────────
        var hmacKey = GetHmacKey(deployment.Target);
        if (hmacKey is not null)
        {
            var manifestEntry = archive.GetEntry("manifest.json")
                ?? throw new InvalidOperationException(
                    "Result bundle missing manifest.json — cannot verify HMAC.");

            var signatureEntry = archive.GetEntry("signature.bin")
                ?? throw new InvalidOperationException(
                    "Result bundle missing signature.bin — HMAC verification required.");

            var manifestBytes = await ReadEntryBytesAsync(manifestEntry, ct).ConfigureAwait(false);
            var signatureBytes = await ReadEntryBytesAsync(signatureEntry, ct).ConfigureAwait(false);

            var expectedSig = HMACSHA256.HashData(hmacKey, manifestBytes);
            if (!CryptographicOperations.FixedTimeEquals(signatureBytes, expectedSig))
            {
                throw new InvalidOperationException(
                    "HMAC signature verification failed — the result bundle may have been tampered with.");
            }

            logger.LogInformation("HMAC signature verified for deployment {Id}.", deploymentId);
        }

        // ── Bundle-format guard ─────────────────────────────────────────────
        // The result shape is tied to the bundle format. Refuse a result from a
        // bundle this server version doesn't understand (e.g. an older,
        // pre-encrypted-plan bundle with no bundleFormat) rather than silently
        // mis-recording it (its result JSON has no `success` key → would default
        // to a false Failed).
        var manifestEntryForFormat = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("Result bundle is missing manifest.json.");
        var manifestJsonForFormat = await ReadEntryTextAsync(manifestEntryForFormat, ct).ConfigureAwait(false);
        var bundleFormat = JsonSerializer.Deserialize<ManifestFormatProbe>(manifestJsonForFormat, JsonOpts)?.BundleFormat ?? 0;
        if (bundleFormat != DropBundleService.BundleFormat)
        {
            throw new InvalidOperationException(
                $"Result bundle format {bundleFormat} is not supported by this server " +
                $"(expected {DropBundleService.BundleFormat}). Re-create the offline drop for this deployment.");
        }

        // Maps a sanitized artifact-dir segment back to the real step name so an
        // artifact's StepName matches its step-outcome row even when the step
        // name contains characters the on-disk dir can't hold (the runner writes
        // artifacts under a sanitized dir; this reverses it via the result).
        var stepNameBySanitizedDir = new Dictionary<string, string>(StringComparer.Ordinal);

        // ── Verify + parse result ───────────────────────────────────────────
        // The offline runner writes an OfflineDropResult (overall success +
        // per-step outcomes + output variables). We ingest the same step-outcome
        // and output-variable rows an online deployment produces, so the
        // Steps/Variables tabs render identically.
        var resultEntry = archive.GetEntry(OfflineBundleLayout.ResultFile);
        if (resultEntry is null)
        {
            // No result file — mark as succeeded by convention.
            deployment.Status = DeploymentStatus.Succeeded;
            deployment.CompletedUtc = DateTimeOffset.UtcNow;
        }
        else
        {
            var resultBytes = await ReadEntryBytesAsync(resultEntry, ct).ConfigureAwait(false);

            // The result drives DB writes, and it travels back over an untrusted
            // channel — verify its signature against the per-target bundle key
            // when one is configured.
            var bundleKey = GetBundleKey(deployment.Target);
            if (bundleKey is not null)
            {
                var sigEntry = archive.GetEntry(OfflineBundleLayout.ResultSignatureFile)
                    ?? throw new InvalidOperationException(
                        "Result bundle is missing result-signature.bin — result integrity check required.");
                var resultSig = await ReadEntryBytesAsync(sigEntry, ct).ConfigureAwait(false);
                if (!OfflineResultSigner.Verify(bundleKey, resultBytes, resultSig))
                {
                    throw new InvalidOperationException(
                        "Result signature verification failed — deployment-result.json may have been tampered with.");
                }
            }

            var result = JsonSerializer.Deserialize<OfflineDropResult>(resultBytes, JsonOpts);
            if (result is null)
            {
                deployment.Status = DeploymentStatus.Succeeded;
                deployment.CompletedUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                var completedUtc = result.CompletedUtc ?? DateTimeOffset.UtcNow;
                // A non-Required step failure leaves Success=true but warrants the
                // yellow SucceededWithWarnings, matching the online path.
                var anyNonSkippedFailure = result.Steps.Any(s => !s.Skipped && !s.Success);
                deployment.Status = !result.Success
                    ? DeploymentStatus.Failed
                    : anyNonSkippedFailure
                        ? DeploymentStatus.SucceededWithWarnings
                        : DeploymentStatus.Succeeded;
                deployment.CompletedUtc = completedUtc;

                foreach (var step in result.Steps)
                {
                    stepNameBySanitizedDir[OfflineBundleLayout.SanitizeStepName(step.StepName)] = step.StepName;

                    var outcome = step.Skipped
                        ? StepOutcomeKind.Skipped
                        : step.Success ? StepOutcomeKind.Succeeded : StepOutcomeKind.Failed;
                    db.Set<DeploymentStepOutcome>().Add(new DeploymentStepOutcome
                    {
                        DeploymentId = deploymentId,
                        StepIndex    = step.StepIndex,
                        StepName     = step.StepName,
                        Outcome      = outcome,
                        AttemptCount = 1,
                        ErrorMessage = step.Success || step.Skipped ? null : step.ErrorMessage,
                        StartedUtc   = step.Skipped ? null : completedUtc,
                        CompletedUtc = completedUtc,
                        IsServerSide = false,
                        Required     = step.Required,
                        TargetId     = deployment.TargetId,
                    });

                    foreach (var (name, value) in step.OutputVariables)
                    {
                        db.Set<DeploymentOutputVariable>().Add(new DeploymentOutputVariable
                        {
                            DeploymentId = deploymentId,
                            StepName     = step.StepName,
                            Name         = name,
                            Value        = value,
                            CapturedUtc  = completedUtc,
                        });
                    }
                }
            }
        }

        // ── Parse log ───────────────────────────────────────────────────────
        var logEntry = archive.GetEntry("deployment-log.txt");
        if (logEntry is not null)
        {
            var logText = await ReadEntryTextAsync(logEntry, ct).ConfigureAwait(false);
            var lines = logText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var seq = deployment.NextLogSequence;

            foreach (var line in lines)
            {
                var trimmed = line.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                {
                    continue; // skip comments and blank lines
                }

                // Try to parse structured format: "timestamp | level | message"
                var (level, message) = ParseLogLine(trimmed);

                db.Set<DeploymentLogEntry>().Add(new DeploymentLogEntry
                {
                    DeploymentId = deploymentId,
                    Sequence = seq++,
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = level,
                    Message = message,
                });
            }

            deployment.NextLogSequence = seq;
        }

        // ── Process artifacts ────────────────────────────────────────────────
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("artifacts/", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.Equals("artifacts/", StringComparison.OrdinalIgnoreCase) ||
                entry.Length == 0)
            {
                continue;
            }

            var fileName = Path.GetFileName(entry.FullName);
            var sanitizedSegment = Path.GetDirectoryName(entry.FullName)?
                .Replace("artifacts/", "", StringComparison.OrdinalIgnoreCase)
                .Replace("artifacts\\", "", StringComparison.OrdinalIgnoreCase)
                .Trim('/', '\\');

            if (string.IsNullOrEmpty(sanitizedSegment))
            {
                sanitizedSegment = "offline";
            }

            // The dir is a sanitized form of the step name — recover the real
            // name from the result so the artifact's StepName matches its step
            // outcome row (falls back to the sanitized segment if unmapped).
            var stepName = stepNameBySanitizedDir.GetValueOrDefault(sanitizedSegment, sanitizedSegment);

            await using var artifactStream = entry.Open();
            var storedPath = await artifactStore
                .SaveAsync(deploymentId, stepName, fileName, artifactStream, ct)
                .ConfigureAwait(false);

            var contentType = MimeMapping.GetContentType(fileName);
            db.Set<DeploymentArtifact>().Add(new DeploymentArtifact
            {
                DeploymentId = deploymentId,
                StepName = stepName,
                FileName = fileName,
                ContentType = contentType,
                SizeBytes = entry.Length,
                StoredPath = storedPath,
                CollectedUtc = DateTimeOffset.UtcNow,
            });

            logger.LogInformation(
                "Ingested artifact '{File}' ({Size} bytes) from result bundle for deployment {Id}.",
                fileName, entry.Length, deploymentId);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Offline result ingested for deployment {Id}: status = {Status}.",
            deploymentId, deployment.Status);

        return deployment;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private byte[]? GetHmacKey(DeploymentTarget? target)
    {
        var hmacEncrypted = target?.OfflineDropConfig?.HmacKeyEncrypted;
        if (string.IsNullOrEmpty(hmacEncrypted))
        {
            return null;
        }

        var base64Key = encryption.Decrypt(hmacEncrypted);
        return Convert.FromBase64String(base64Key);
    }

    private byte[]? GetBundleKey(DeploymentTarget? target)
    {
        var enc = target?.OfflineDropConfig?.BundleKeyEncrypted;
        if (string.IsNullOrEmpty(enc))
        {
            return null;
        }
        return Convert.FromBase64String(encryption.Decrypt(enc));
    }

    private static (string Level, string Message) ParseLogLine(string line)
    {
        // Try: "timestamp | level | message"
        var parts = line.Split('|', 3);
        if (parts.Length == 3)
        {
            var level = parts[1].Trim().ToLowerInvariant() switch
            {
                "info" or "information" => "info",
                "warn" or "warning"     => "warning",
                "error" or "err"        => "error",
                _                       => "info",
            };
            return (level, parts[2].Trim());
        }

        // Fallback: whole line is the message
        return ("info", line);
    }

    private static async Task<byte[]> ReadEntryBytesAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var stream = entry.Open();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task<string> ReadEntryTextAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        var bytes = await ReadEntryBytesAsync(entry, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>Minimal projection to read the bundle format from manifest.json.</summary>
internal sealed class ManifestFormatProbe
{
    public int BundleFormat { get; set; }
}

/// <summary>Simple MIME type lookup for artifacts.</summary>
internal static class MimeMapping
{
    public static string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".log"        => "text/plain",
            ".json"                 => "application/json",
            ".xml"                  => "application/xml",
            ".html" or ".htm"       => "text/html",
            ".zip"                  => "application/zip",
            ".png"                  => "image/png",
            ".jpg" or ".jpeg"       => "image/jpeg",
            ".gif"                  => "image/gif",
            ".pdf"                  => "application/pdf",
            ".csv"                  => "text/csv",
            _                       => "application/octet-stream",
        };
    }
}
