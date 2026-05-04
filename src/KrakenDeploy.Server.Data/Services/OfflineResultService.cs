using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    KrakenDbContext db,
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

        // ── Parse result ────────────────────────────────────────────────────
        var resultEntry = archive.GetEntry("deployment-result.json");
        if (resultEntry is not null)
        {
            var resultJson = await ReadEntryTextAsync(resultEntry, ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<OfflineResult>(resultJson, JsonOpts);
            if (result is not null)
            {
                deployment.Status = ParseStatus(result.Status);
                if (result.CompletedUtc.HasValue)
                {
                    deployment.CompletedUtc = result.CompletedUtc.Value;
                }
                else
                {
                    deployment.CompletedUtc = DateTimeOffset.UtcNow;
                }
            }
        }
        else
        {
            // No result file — mark as succeeded by convention
            deployment.Status = DeploymentStatus.Succeeded;
            deployment.CompletedUtc = DateTimeOffset.UtcNow;
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
            var stepName = Path.GetDirectoryName(entry.FullName)?
                .Replace("artifacts/", "", StringComparison.OrdinalIgnoreCase)
                .Replace("artifacts\\", "", StringComparison.OrdinalIgnoreCase)
                .Trim('/', '\\');

            if (string.IsNullOrEmpty(stepName))
            {
                stepName = "offline";
            }

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

    private static DeploymentStatus ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return DeploymentStatus.Succeeded;
        }

        return status.Trim().ToLowerInvariant() switch
        {
            "succeeded" or "success" => DeploymentStatus.Succeeded,
            "failed" or "failure"    => DeploymentStatus.Failed,
            _                        => DeploymentStatus.Succeeded,
        };
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

/// <summary>Deserialized from <c>deployment-result.json</c> in the result bundle.</summary>
internal sealed class OfflineResult
{
    public Guid DeploymentId { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
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
