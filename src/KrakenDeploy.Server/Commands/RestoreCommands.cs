using System.Diagnostics;
using System.Text.Json;
using KrakenDeploy.ControlPlane;
using KrakenDeploy.Server.Core.Domain.Accounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Commands;

/// <summary>
/// CLI subcommand <c>restore</c>. Restores a backup bundle created by <c>backup</c> —
/// runs the SQL dump and copies the data directory back.
/// <para>
/// Multi-account: pass <c>--account &lt;subdomain&gt;</c> to restore into that account's
/// tenant database + file slice (<c>{DataPath}/accounts/{accountId}</c>). The bundle's
/// manifest records the account it was taken from, and restore <b>refuses</b> to load a
/// bundle into a different account (prevents a catastrophic cross-tenant overwrite).
/// Single-instance: omit <c>--account</c> — restores into the configured <c>KrakenDb</c>
/// and the flat data directory exactly as before.
/// </para>
/// </summary>
internal static class RestoreCommands
{
    public static async Task<int> RunAsync(string[] args, string contentRoot)
    {
        string? from = null;
        string? account = null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--from") { from = args[i + 1]; }
            else if (args[i] == "--account") { account = args[i + 1]; }
        }

        if (from is null)
        {
            Console.Error.WriteLine("Usage: restore --from <backup-directory> [--account <subdomain>]");
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

        // ── Resolve the target database + data root ─────────────────────────────
        var builder = CliHost.CreateBuilder(contentRoot);
        var multiAccount = builder.Configuration.GetValue("MultiAccount:Enabled", false);

        string connectionString;
        string dataTargetRoot;

        if (multiAccount)
        {
            if (account is null)
            {
                Console.Error.WriteLine(
                    "Multi-account mode: --account <subdomain> is required so the bundle is " +
                    "restored into the correct tenant database.");
                return 1;
            }

            var catalogConn = builder.Configuration.GetConnectionString("Catalog");
            if (string.IsNullOrWhiteSpace(catalogConn))
            {
                Console.Error.WriteLine("ConnectionStrings:Catalog is not configured (required for --account).");
                return 1;
            }

            var baseDomain = builder.Configuration["MultiAccount:BaseDomain"] ?? "localhost";
            var dataPath = builder.Configuration["Server:DataPath"] ?? "data";

            builder.Services.AddKrakenControlPlane(builder.Configuration, catalogConn, dataPath);

            using var app = builder.Build();
            await using var scope = app.Services.CreateAsyncScope();
            var resolver = scope.ServiceProvider.GetRequiredService<IAccountResolver>();
            var resolved = await resolver.ResolveAsync($"{account}.{baseDomain}").ConfigureAwait(false);
            if (resolved is null)
            {
                Console.Error.WriteLine($"Account '{account}' was not found or is not active in the catalog.");
                return 1;
            }

            // Cross-account safety: never restore a bundle into a different account.
            if (manifest.Account is not null &&
                !string.Equals(manifest.Account.Subdomain, resolved.Subdomain, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"Refusing to restore: this bundle belongs to account '{manifest.Account.Subdomain}', " +
                    $"not '{resolved.Subdomain}'. Restoring it would overwrite the wrong tenant.");
                return 1;
            }

            if (manifest.Account is null)
            {
                Console.Error.WriteLine(
                    $"Warning: bundle has no account stamp (older or single-instance backup) — " +
                    $"cannot verify it belongs to '{account}'. Proceeding into account '{resolved.Subdomain}'.");
            }

            connectionString = resolved.ConnectionString;
            dataTargetRoot = Path.Combine(dataPath, "accounts", resolved.Id.ToString());
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
            dataTargetRoot = builder.Configuration["Server:DataPath"] ?? "data";
        }

        // ── Restore database ───────────────────────────────────────────────────
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

        Console.WriteLine($"Database restored into '{db}'.");

        // ── Restore data directory ─────────────────────────────────────────────
        if (manifest.DataDirectory is not null)
        {
            var sourceData = Path.Combine(from, manifest.DataDirectory);
            if (Directory.Exists(sourceData))
            {
                if (Directory.Exists(dataTargetRoot))
                {
                    Console.Write($"Data directory '{dataTargetRoot}' already exists. Overwrite? [y/N]: ");
                    var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                    if (answer != "y" && answer != "yes")
                    {
                        Console.WriteLine("Skipping data directory restore.");
                        Console.WriteLine("Restore complete (database only).");
                        return 0;
                    }
                }

                CopyDirectoryRecursive(sourceData, dataTargetRoot);
                Console.WriteLine($"Data directory restored into '{dataTargetRoot}'.");
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
        string? DataDirectory,
        BackupAccount? Account);

    /// <summary>The account a multi-account bundle belongs to (null single-instance).</summary>
    private sealed record BackupAccount(string Subdomain, Guid Id);
}
