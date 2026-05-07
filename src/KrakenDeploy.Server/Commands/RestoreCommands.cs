using System.Diagnostics;
using System.Text.Json;
using KrakenDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace KrakenDeploy.Server.Commands;

/// <summary>
/// CLI subcommand <c>restore</c>. Restores a backup bundle created by
/// <c>backup</c> — runs the SQL dump and copies the data directory back.
/// </summary>
internal static class RestoreCommands
{
    public static async Task<int> RunAsync(string[] args, string contentRoot)
    {
        string? from = null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--from")
            {
                from = args[i + 1];
            }
        }

        if (from is null)
        {
            Console.Error.WriteLine("Usage: restore --from <backup-directory>");
            return 1;
        }

        if (!Directory.Exists(from))
        {
            Console.Error.WriteLine($"Backup directory not found: {from}");
            return 1;
        }

        // ── Validate manifest ──────────────────────────────────────────────────
        var manifestPath = Path.Combine(from, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine("manifest.json not found — not a valid backup bundle.");
            return 1;
        }

        var manifestJson = await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false);
        BackupManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(manifestJson);
        }
        catch
        {
            Console.Error.WriteLine("manifest.json is corrupt — cannot restore.");
            return 1;
        }

        if (manifest is null)
        {
            Console.Error.WriteLine("manifest.json is empty — aborting.");
            return 1;
        }

        // ── Version check ──────────────────────────────────────────────────────
        var currentVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        if (!string.Equals(manifest.ServerVersion, currentVersion, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"Version mismatch: backup is from v{manifest.ServerVersion}, " +
                $"current is v{currentVersion}. Restore may fail — downgrade the " +
                "server binary to the backup version first.");
            return 1;
        }

        // ── Restore database ───────────────────────────────────────────────────
        var builder = CliHost.CreateBuilder(contentRoot);
        var connectionString = builder.Configuration.GetConnectionString("KrakenDb");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine(
                "ConnectionStrings:KrakenDb is not configured. " +
                "Set it in appsettings.{Environment}.json or via env var.");
            return 1;
        }

        var csBuilder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        var host = csBuilder.Host ?? "localhost";
        var db = csBuilder.Database ?? "krakendeploy";
        var port = csBuilder.Port;
        var username = csBuilder.Username;
        var password = csBuilder.Password;

        var dumpFile = Path.Combine(from, manifest.DatabaseFile);

        if (!File.Exists(dumpFile))
        {
            Console.Error.WriteLine($"Database dump not found: {dumpFile}");
            return 1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "psql",
            Arguments = $"--host={host} --port={port} " +
                        $"--username={username} --dbname={db} " +
                        $"-v ON_ERROR_STOP=1 -f \"{dumpFile}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["PGPASSWORD"] = password;

        using var psql = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start psql.");

        var stdErr = await psql.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await psql.WaitForExitAsync().ConfigureAwait(false);

        if (psql.ExitCode != 0)
        {
            Console.Error.WriteLine($"psql restore failed (exit {psql.ExitCode}):");
            Console.Error.WriteLine(stdErr);
            return 1;
        }

        Console.WriteLine("Database restored.");

        // ── Restore data directory ─────────────────────────────────────────────
        if (manifest.DataDirectory is not null)
        {
            var sourceData = Path.Combine(from, manifest.DataDirectory);
            if (Directory.Exists(sourceData))
            {
                var dataPath = builder.Configuration["Server:DataPath"] ?? "data";
                if (Directory.Exists(dataPath))
                {
                    Console.Write($"Data directory '{dataPath}' already exists. Overwrite? [y/N]: ");
                    var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                    if (answer != "y" && answer != "yes")
                    {
                        Console.WriteLine("Skipping data directory restore.");
                        Console.WriteLine("Restore complete (database only).");
                        return 0;
                    }
                }

                CopyDirectoryRecursive(sourceData, dataPath);
                Console.WriteLine("Data directory restored.");
            }
        }

        Console.WriteLine("Restore complete.");
        return 0;
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
        string? DataDirectory);
}
