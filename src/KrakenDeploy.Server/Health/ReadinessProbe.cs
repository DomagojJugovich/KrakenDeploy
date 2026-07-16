using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Health;

/// <summary>
/// C3/P1 — the depth behind <c>/health/ready</c>. Unlike <c>/healthz</c> (a
/// shallow liveness probe: process up + DB reachable), readiness answers "can
/// this instance actually serve a deployment right now?" — so an orchestrator /
/// load balancer stops routing traffic to a node that is up but degraded.
/// <para>
/// It probes the three prerequisites a deployment needs:
/// <list type="number">
/// <item><b>Database</b> — reachable.</item>
/// <item><b>Encryption</b> — an encrypt→decrypt round-trip through the DEK. In
/// Production the DEK is NOT eagerly loaded at boot (EnsureDekAsync is
/// Development-only), so a wrong KEK / bricked DEK (C2) otherwise surfaces only
/// at the first secret access mid-deployment. This probe forces that unwrap and
/// reports unready instead.</item>
/// <item><b>Data directory</b> — writable. Packages, the offline drop bundle and
/// the Data-Protection ring all land under <c>Server:DataPath</c>; an unwritable
/// or full volume (T0-9) breaks deployments while the process stays "up".</item>
/// </list>
/// </para>
/// Registered as a singleton: it depends only on the singleton
/// <see cref="IEncryptionService"/> and a path string. The scoped
/// <see cref="KrakenDbContext"/> is passed to <see cref="CheckAsync"/> per call,
/// NOT captured — capturing it would be a captive dependency the all-environment
/// ValidateOnBuild now rejects.
/// </summary>
public sealed class ReadinessProbe(IEncryptionService encryption, string dataPath)
{
    // A fixed, non-secret marker; the value never leaves the process.
    private const string ProbePlaintext = "kraken-readiness-probe";

    public async Task<ReadinessResult> CheckAsync(KrakenDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var database = await ProbeDatabaseAsync(db, ct).ConfigureAwait(false);
        var encryptionOk = ProbeEncryption(out var encryptionError);
        var dataDirectoryOk = ProbeDataDirectory(out var dataDirectoryError);

        var details = new List<string>(3);
        if (!database) { details.Add("database unreachable"); }
        if (encryptionError is not null) { details.Add(encryptionError); }
        if (dataDirectoryError is not null) { details.Add(dataDirectoryError); }

        return new ReadinessResult(
            Ready: database && encryptionOk && dataDirectoryOk,
            Database: database,
            Encryption: encryptionOk,
            DataDirectory: dataDirectoryOk,
            Detail: details.Count == 0 ? null : string.Join("; ", details));
    }

    private static async Task<bool> ProbeDatabaseAsync(KrakenDbContext db, CancellationToken ct)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Encrypt→decrypt round-trip through the DEK. Returns false (and a
    /// sanitised reason) on any crypto failure — the message carries only the
    /// exception type, never key material or ciphertext.</summary>
    public bool ProbeEncryption(out string? error)
    {
        try
        {
            var roundTripped = encryption.Decrypt(encryption.Encrypt(ProbePlaintext));
            if (!string.Equals(roundTripped, ProbePlaintext, StringComparison.Ordinal))
            {
                error = "encryption round-trip mismatch";
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            // Bricked DEK (CryptographicException) or no DEK provisioned
            // (InvalidOperationException) both mean "cannot serve secrets".
            error = $"encryption unavailable ({ex.GetType().Name})";
            return false;
        }
    }

    /// <summary>Write-then-delete a unique probe file under
    /// <c>Server:DataPath</c>. Returns false on any IO/permission failure.</summary>
    public bool ProbeDataDirectory(out string? error)
    {
        var probeFile = Path.Combine(dataPath, $".readiness-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(dataPath);
            File.WriteAllText(probeFile, "ok");
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"data directory not writable ({ex.GetType().Name})";
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probeFile)) { File.Delete(probeFile); }
            }
            catch
            {
                // Best-effort cleanup; a leftover probe file must not flip readiness.
            }
        }
    }
}

/// <summary>Outcome of a <see cref="ReadinessProbe"/> run. Per-probe booleans are
/// safe to expose; <see cref="Detail"/> is a sanitised summary (no secrets).</summary>
public sealed record ReadinessResult(
    bool Ready,
    bool Database,
    bool Encryption,
    bool DataDirectory,
    string? Detail);
