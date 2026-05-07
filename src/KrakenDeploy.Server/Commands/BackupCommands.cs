using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using KrakenDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace KrakenDeploy.Server.Commands;

/// <summary>
/// CLI subcommand <c>backup</c>. Creates a timestamped backup bundle containing
/// a pg_dump of the database and a copy of the server's data directory.
/// </summary>
internal static class BackupCommands
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args, string contentRoot)
    {
        string? to = null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--to")
            {
                to = args[i + 1];
            }
        }

        if (to is null)
        {
            Console.Error.WriteLine("Usage: backup --to <output-directory>");
            return 1;
        }

        var builder = CliHost.CreateBuilder(contentRoot);
        var connectionString = builder.Configuration.GetConnectionString("KrakenDb");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine(
                "ConnectionStrings:KrakenDb is not configured. " +
                "Set it in appsettings.{Environment}.json or via env var.");
            return 1;
        }

        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var backupDir = Path.Combine(to, $"kraken-backup-{timestamp}");
            Directory.CreateDirectory(backupDir);

            var pgDumpPath = FindPgDump();
            if (pgDumpPath is null)
            {
                Console.Error.WriteLine(
                    "pg_dump not found. Install PostgreSQL client tools " +
                    "or make sure pg_dump is on PATH.");
                return 1;
            }

            var csBuilder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
            var host = csBuilder.Host ?? "localhost";
            var db = csBuilder.Database ?? "krakendeploy";
            var port = csBuilder.Port;
            var username = csBuilder.Username;
            var password = csBuilder.Password;

            var dumpFile = Path.Combine(backupDir, "database.sql");
            var psi = new ProcessStartInfo
            {
                FileName = pgDumpPath,
                Arguments = $"--host={host} --port={port} " +
                            $"--username={username} --dbname={db} " +
                            $"--no-password --no-owner --clean --if-exists",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.EnvironmentVariables["PGPASSWORD"] = password;

            using var pgDump = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start pg_dump.");

            await using var outStream = File.Create(dumpFile);
            await pgDump.StandardOutput.BaseStream.CopyToAsync(outStream).ConfigureAwait(false);
            await pgDump.WaitForExitAsync().ConfigureAwait(false);

            if (pgDump.ExitCode != 0)
            {
                var err = await pgDump.StandardError.ReadToEndAsync().ConfigureAwait(false);
                Console.Error.WriteLine($"pg_dump failed (exit {pgDump.ExitCode}): {err}");
                return 1;
            }

            Console.WriteLine($"Database dumped to {dumpFile}");

            var dataPath = builder.Configuration["Server:DataPath"] ?? "data";
            if (Directory.Exists(dataPath))
            {
                var dataBackupDir = Path.Combine(backupDir, "data");
                CopyDirectoryRecursive(dataPath, dataBackupDir);
                Console.WriteLine($"Data directory copied to {dataBackupDir}");
            }
            else
            {
                Console.WriteLine("No data/ directory found — skipping.");
            }

            var serverVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            var manifest = new BackupManifest(
                Timestamp: timestamp,
                ServerVersion: serverVersion,
                DatabaseFile: "database.sql",
                DataDirectory: Directory.Exists(dataPath) ? "data" : null,
                ConnectionInfo: new BackupConnectionInfo(host, port, db));
            var manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            await File.WriteAllTextAsync(Path.Combine(backupDir, "manifest.json"), manifestJson)
                .ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"Backup complete: {backupDir}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Backup failed: {ex.Message}");
            return 1;
        }
    }

    private static string? FindPgDump()
    {
        var commonPaths = new[]
        {
            @"C:\Program Files\PostgreSQL\16\bin\pg_dump.exe",
            @"C:\Program Files\PostgreSQL\15\bin\pg_dump.exe",
            @"/usr/bin/pg_dump",
            @"/usr/local/bin/pg_dump",
        };

        foreach (var p in commonPaths)
        {
            if (File.Exists(p))
            {
                return p;
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = "pg_dump",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
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
        catch
        {
            // PATH resolution failed — pg_dump not available.
        }

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
