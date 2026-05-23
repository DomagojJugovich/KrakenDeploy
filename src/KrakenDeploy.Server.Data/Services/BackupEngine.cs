using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// In-process implementation of the same backup logic the CLI runs
/// (<see cref="KrakenDeploy.Server.Commands.BackupCommands"/>). Lifted out
/// so the UI's "Backup now" button and the Hangfire scheduled job invoke
/// the same code path the on-prem-guide cron job uses.
///
/// <para>
/// Bundle shape (unchanged from the CLI): a sibling-of-target directory
/// called <c>kraken-backup-{yyyyMMdd-HHmmss}</c> containing:
/// <list type="bullet">
///   <item><c>database.sql</c> — pg_dump --clean --if-exists</item>
///   <item><c>data/</c> — recursive copy of <c>Server:DataPath</c></item>
///   <item><c>manifest.json</c> — timestamp + server version + connection info</item>
/// </list>
/// </para>
/// </summary>
public sealed class BackupEngine(
    IConfiguration configuration,
    ILogger<BackupEngine> logger,
    TimeProvider time)
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Runs one backup into <paramref name="targetDirectory"/>. Returns
    /// the result record (bundle path + size on success, error message on
    /// failure). Does NOT throw — exceptions are captured into the result
    /// so the UI and the scheduled job can render them consistently.
    /// </summary>
    public async Task<BackupResult> RunAsync(
        string targetDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        var started = time.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var connectionString = configuration.GetConnectionString("KrakenDb");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return Fail(started, stopwatch.Elapsed,
                    "ConnectionStrings:KrakenDb is not configured.");
            }

            var pgDumpPath = FindPgDump();
            if (pgDumpPath is null)
            {
                return Fail(started, stopwatch.Elapsed,
                    "pg_dump not found. Install PostgreSQL client tools or " +
                    "make sure pg_dump is on PATH.");
            }

            // Bundle layout matches the CLI exactly so existing restore
            // tooling + on-prem-guide cron jobs stay compatible.
            var timestamp = started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var bundleDir = Path.Combine(targetDirectory, $"kraken-backup-{timestamp}");
            Directory.CreateDirectory(bundleDir);

            var csBuilder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
            var host     = csBuilder.Host     ?? "localhost";
            var db       = csBuilder.Database ?? "krakendeploy";
            var port     = csBuilder.Port;
            var username = csBuilder.Username;
            var password = csBuilder.Password;

            // ── pg_dump ────────────────────────────────────────────────
            var dumpFile = Path.Combine(bundleDir, "database.sql");
            var psi = new ProcessStartInfo
            {
                FileName  = pgDumpPath,
                Arguments = $"--host={host} --port={port} " +
                            $"--username={username} --dbname={db} " +
                            $"--no-password --no-owner --clean --if-exists",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.EnvironmentVariables["PGPASSWORD"] = password;

            using var pgDump = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start pg_dump.");
            await using (var outStream = File.Create(dumpFile))
            {
                await pgDump.StandardOutput.BaseStream.CopyToAsync(outStream, ct)
                    .ConfigureAwait(false);
            }
            await pgDump.WaitForExitAsync(ct).ConfigureAwait(false);
            if (pgDump.ExitCode != 0)
            {
                var err = await pgDump.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                return Fail(started, stopwatch.Elapsed,
                    $"pg_dump failed (exit {pgDump.ExitCode}): {err}");
            }

            // ── Data directory ─────────────────────────────────────────
            var dataPath = configuration["Server:DataPath"] ?? "data";
            if (Directory.Exists(dataPath))
            {
                var dataBackupDir = Path.Combine(bundleDir, "data");
                CopyDirectoryRecursive(dataPath, dataBackupDir);
            }

            // ── Manifest ───────────────────────────────────────────────
            var serverVersion = typeof(BackupEngine).Assembly
                .GetName().Version?.ToString() ?? "0.0.0";
            var manifest = new BackupManifest(
                Timestamp:     timestamp,
                ServerVersion: serverVersion,
                DatabaseFile:  "database.sql",
                DataDirectory: Directory.Exists(dataPath) ? "data" : null,
                ConnectionInfo: new BackupConnectionInfo(host, port, db));
            var manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            await File.WriteAllTextAsync(Path.Combine(bundleDir, "manifest.json"),
                manifestJson, ct).ConfigureAwait(false);

            var size = MeasureDirectory(new DirectoryInfo(bundleDir));
            stopwatch.Stop();
            logger.LogInformation(
                "Backup complete: {Bundle} ({Size:F1} MiB, {Elapsed:F0} ms)",
                bundleDir, size / (1024.0 * 1024), stopwatch.Elapsed.TotalMilliseconds);

            return new BackupResult(
                Succeeded:   true,
                StartedUtc:  started,
                Elapsed:     stopwatch.Elapsed,
                BundlePath:  bundleDir,
                BundleBytes: size,
                Error:       null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "Backup failed: {Message}", ex.Message);
            return Fail(started, stopwatch.Elapsed, ex.Message);
        }
    }

    /// <summary>
    /// Deletes all but the most recent <paramref name="keepLast"/> bundles in
    /// <paramref name="targetDirectory"/>. Bundles are identified by the
    /// <c>kraken-backup-*</c> directory-name pattern; anything else in the
    /// folder is left alone. Caller's responsibility to not call this with
    /// keepLast=0 (the service layer guards against it).
    /// </summary>
    public int PruneOldBundles(string targetDirectory, int keepLast)
    {
        if (keepLast <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keepLast),
                "Prune retention must be positive; pass 0 in BackupSettings to disable pruning instead.");
        }
        if (!Directory.Exists(targetDirectory)) { return 0; }

        var bundles = Directory.GetDirectories(targetDirectory, "kraken-backup-*")
            .OrderByDescending(d => d)        // timestamp sort → newest first
            .Skip(keepLast)
            .ToList();
        foreach (var b in bundles)
        {
            try { Directory.Delete(b, recursive: true); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete old bundle {Path}", b);
            }
        }
        return bundles.Count;
    }

    private static BackupResult Fail(DateTimeOffset startedUtc, TimeSpan elapsed, string message)
        => new(Succeeded:   false,
               StartedUtc:  startedUtc,
               Elapsed:     elapsed,
               BundlePath:  null,
               BundleBytes: 0,
               Error:       message);

    private static long MeasureDirectory(DirectoryInfo dir)
    {
        long size = 0;
        foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try { size += f.Length; } catch { /* concurrent delete; ignore */ }
        }
        return size;
    }

    private static string? FindPgDump()
    {
        // Same probe order as the CLI — keep them in sync so an operator
        // doesn't see different lookup behaviour between the two surfaces.
        var commonPaths = new[]
        {
            @"C:\Program Files\PostgreSQL\16\bin\pg_dump.exe",
            @"C:\Program Files\PostgreSQL\15\bin\pg_dump.exe",
            @"/usr/bin/pg_dump",
            @"/usr/local/bin/pg_dump",
        };
        foreach (var p in commonPaths)
        {
            if (File.Exists(p)) { return p; }
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName  = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = "pg_dump",
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var result = proc.StandardOutput.ReadLine();
                proc.WaitForExit();
                if (!string.IsNullOrWhiteSpace(result) && File.Exists(result))
                {
                    return result;
                }
            }
        }
        catch { /* PATH probe failed — pg_dump just isn't available. */ }
        return null;
    }

    private static void CopyDirectoryRecursive(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectoryRecursive(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }

    private sealed record BackupManifest(
        string Timestamp,
        string ServerVersion,
        string DatabaseFile,
        string? DataDirectory,
        BackupConnectionInfo ConnectionInfo);

    private sealed record BackupConnectionInfo(string Host, int Port, string Database);
}

/// <summary>Result of a single backup run. Mutually-exclusive
/// success/failure shape (BundlePath set on success, Error set on failure).</summary>
public sealed record BackupResult(
    bool Succeeded,
    DateTimeOffset StartedUtc,
    TimeSpan Elapsed,
    string? BundlePath,
    long BundleBytes,
    string? Error);
