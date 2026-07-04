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
        string? account = null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--to")
            {
                to = args[i + 1];
            }
            else if (args[i] == "--account")
            {
                account = args[i + 1];
            }
        }

        if (to is null)
        {
            Console.Error.WriteLine("Usage: backup --to <output-directory> [--account <subdomain>]");
            return 1;
        }

        var builder = CliHost.CreateBuilder(contentRoot);
        var multiAccount = builder.Configuration.GetValue("MultiAccount:Enabled", false);
        var dataPath = builder.Configuration["Server:DataPath"] ?? "data";

        string connectionString;
        string dataSourceRoot;
        BackupAccount? manifestAccount;

        if (multiAccount)
        {
            // Symmetric with `restore --account`: dump the tenant's own database and
            // back up only its file slice, stamping the manifest so restore can verify
            // the bundle before loading it into a tenant.
            var resolved = await CliHost.ResolveTenantAccountAsync(contentRoot, account).ConfigureAwait(false);
            if (resolved is null)
            {
                return 1; // the resolver already printed the reason
            }

            connectionString = resolved.ConnectionString;
            // NOT the flat data root — in multi-account that holds every tenant's files
            // plus the control-plane secret store. Restore expects this same slice.
            dataSourceRoot = Path.Combine(dataPath, "accounts", resolved.Id.ToString());
            manifestAccount = new BackupAccount(resolved.Subdomain, resolved.Id);
        }
        else
        {
            var cs = builder.Configuration.GetConnectionString("KrakenDb");
            if (string.IsNullOrWhiteSpace(cs))
            {
                Console.Error.WriteLine(
                    "ConnectionStrings:KrakenDb is not configured. " +
                    "Set it in appsettings.{Environment}.json or via env var.");
                return 1;
            }

            connectionString = cs;
            dataSourceRoot = dataPath;
            manifestAccount = null;
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

            if (Directory.Exists(dataSourceRoot))
            {
                var dataBackupDir = Path.Combine(backupDir, "data");
                CopyDirectoryRecursive(dataSourceRoot, dataBackupDir);
                Console.WriteLine($"Data directory copied to {dataBackupDir}");
            }
            else
            {
                Console.WriteLine($"No data directory at '{dataSourceRoot}' — skipping.");
            }

            var serverVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            var manifest = new BackupManifest(
                Timestamp: timestamp,
                ServerVersion: serverVersion,
                DatabaseFile: "database.sql",
                DataDirectory: Directory.Exists(dataSourceRoot) ? "data" : null,
                ConnectionInfo: new BackupConnectionInfo(host, port, db),
                Account: manifestAccount);
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
        BackupConnectionInfo ConnectionInfo,
        BackupAccount? Account);

    private sealed record BackupConnectionInfo(string Host, int Port, string Database);

    // Must match RestoreCommands' BackupManifest.Account (same JSON shape) so
    // `restore --account` can verify the bundle belongs to the target tenant.
    // Null for single-instance bundles.
    private sealed record BackupAccount(string Subdomain, Guid Id);
}
