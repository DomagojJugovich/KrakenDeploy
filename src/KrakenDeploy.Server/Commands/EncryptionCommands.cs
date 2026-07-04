using System.Security.Cryptography;
using KrakenDeploy.Contracts.Crypto;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Commands;

/// <summary>
/// <c>encryption</c> CLI verbs (M13.D.2) — envelope key rotation. Both mutating
/// verbs are OFFLINE (refuse if a live server answers), take a safety backup
/// first, and double-confirm.
/// <list type="bullet">
///   <item><c>rotate-kek</c> — re-wrap the DEK under a new KEK. No data walk.
///     Refuses unless the current <c>Encryption:MasterKey</c> unwraps the DEK.</item>
///   <item><c>rotate-dek</c> — new DEK, re-encrypt every secret in one atomic
///     transaction (incl. release-snapshot JSONB).</item>
///   <item><c>status</c> — report DEK presence + whether the current KEK unwraps it.</item>
/// </list>
/// </summary>
internal static class EncryptionCommands
{
    public static async Task<int> RunAsync(string[] args, string contentRoot)
    {
        if (args.Length == 0)
        {
            return PrintTopLevelUsage();
        }

        return args[0] switch
        {
            "rotate-kek" => await RotateKekAsync(args.AsSpan(1).ToArray(), contentRoot).ConfigureAwait(false),
            "rotate-dek" => await RotateDekAsync(args.AsSpan(1).ToArray(), contentRoot).ConfigureAwait(false),
            "status"     => await StatusAsync(args.AsSpan(1).ToArray(), contentRoot).ConfigureAwait(false),
            "--help" or "-h" or "help" => PrintTopLevelUsage(success: true),
            _ => UnknownSubcommand(args[0]),
        };
    }

    // ── rotate-kek ────────────────────────────────────────────────────────────

    private static async Task<int> RotateKekAsync(string[] args, string contentRoot)
    {
        if (!TryParseCommon(args, out var newKey, out var noBackup, out var assumeYes, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        // Headless guard: --yes bypasses both confirmation stages, so an
        // auto-GENERATED KEK would be printed to a log the job may not retain →
        // unrecoverable lockout with no human in the loop. Require the operator
        // to supply --new-key (which they already hold) when running unattended.
        if (assumeYes && newKey is null)
        {
            Console.Error.WriteLine(
                "rotate-kek with --yes requires an explicit --new-key: an auto-generated key would only " +
                "be echoed to output and could be lost unattended, permanently locking the DEK. " +
                "Generate a key you have saved and pass it with --new-key.");
            return 1;
        }

        var builder = CliHost.CreateBuilder(contentRoot);
        if (RefuseIfMultiAccount(builder.Configuration)) { return 1; }
        if (!TryReadKek(builder.Configuration, out var oldKek, out var kekError))
        {
            Console.Error.WriteLine(kekError);
            return 1;
        }
        if (await IsServerRunningAsync(builder.Configuration).ConfigureAwait(false))
        {
            return ServerRunningError();
        }

        var connectionString = builder.Configuration.GetConnectionString("KrakenDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("ConnectionStrings:KrakenDb is not configured.");
            return 1;
        }
        builder.Services.AddKrakenDeployEncryption(Convert.ToBase64String(oldKek));
        builder.Services.AddKrakenDeployData(connectionString);
        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KrakenDbContext>>()
            .CreateDbContextAsync().ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await db.DataEncryptionKeys
                .FirstOrDefaultAsync(k => k.AccountId == null).ConfigureAwait(false);
            if (row is null)
            {
                Console.Error.WriteLine("No data-encryption key exists yet. Run 'database setup' first.");
                return 1;
            }

            // Proves the operator holds the correct current KEK before we let
            // them replace it — otherwise a wrong key would silently orphan the DEK.
            byte[] dek;
            try
            {
                dek = DekProvider.Unwrap(oldKek, row.WrappedDek);
            }
            catch (CryptographicException)
            {
                Console.Error.WriteLine(
                    "The configured Encryption:MasterKey cannot unwrap the stored DEK — refusing. " +
                    "You must rotate with the CURRENT key set in config.");
                return 1;
            }

            var newKek = newKey ?? RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);

            if (!await RunBackupAsync(scope, noBackup).ConfigureAwait(false)) { return 1; }
            if (!ConfirmDestructive("rotate the KEK and re-wrap the DEK", "rotate-kek", assumeYes)) { return 1; }

            row.WrappedDek = DekProvider.Wrap(newKek, dek);
            db.AuditEntries.Add(BuildAudit(AuditEventType.EncryptionKekRotated,
                "KEK rotated; DEK re-wrapped. No data re-encrypted.", "rotate-kek"));
            await db.SaveChangesAsync().ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("KEK rotated. The DEK has been re-wrapped under the NEW KEK.");
            Console.WriteLine();
            Console.WriteLine($"  {Convert.ToBase64String(newKek)}");
            Console.WriteLine();
            Console.WriteLine("Set Encryption:MasterKey to this value NOW, before restarting the server.");
            Console.WriteLine("The server will fail to boot (KEK cannot unwrap the DEK) until config matches.");
            return 0;
        }
    }

    // ── rotate-dek ────────────────────────────────────────────────────────────

    private static async Task<int> RotateDekAsync(string[] args, string contentRoot)
    {
        if (!TryParseCommon(args, out _, out var noBackup, out var assumeYes, out var error, allowNewKey: false))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        // An unattended (--yes) re-encryption of every secret with NO backup
        // leaves zero recovery point if anything goes wrong. Refuse the combo.
        if (assumeYes && noBackup)
        {
            Console.Error.WriteLine(
                "rotate-dek with --yes refuses --no-backup: an unattended re-encryption of every secret " +
                "with no confirmation and no backup leaves no recovery point. Drop --no-backup so a safety " +
                "backup runs, or omit --yes to confirm interactively.");
            return 1;
        }

        var builder = CliHost.CreateBuilder(contentRoot);
        if (RefuseIfMultiAccount(builder.Configuration)) { return 1; }
        if (!TryReadKek(builder.Configuration, out var kek, out var kekError))
        {
            Console.Error.WriteLine(kekError);
            return 1;
        }
        if (await IsServerRunningAsync(builder.Configuration).ConfigureAwait(false))
        {
            return ServerRunningError();
        }

        var connectionString = builder.Configuration.GetConnectionString("KrakenDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("ConnectionStrings:KrakenDb is not configured.");
            return 1;
        }
        builder.Services.AddKrakenDeployEncryption(Convert.ToBase64String(kek));
        builder.Services.AddKrakenDeployData(connectionString);
        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KrakenDbContext>>()
            .CreateDbContextAsync().ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await db.DataEncryptionKeys
                .FirstOrDefaultAsync(k => k.AccountId == null).ConfigureAwait(false);
            if (row is null)
            {
                Console.Error.WriteLine("No data-encryption key exists yet. Run 'database setup' first.");
                return 1;
            }

            byte[] oldDek;
            try
            {
                oldDek = DekProvider.Unwrap(kek, row.WrappedDek);
            }
            catch (CryptographicException)
            {
                Console.Error.WriteLine(
                    "The configured Encryption:MasterKey cannot unwrap the stored DEK — refusing.");
                return 1;
            }

            if (!await RunBackupAsync(scope, noBackup).ConfigureAwait(false)) { return 1; }
            if (!ConfirmDestructive(
                    "generate a NEW data key and re-encrypt EVERY secret", "rotate-dek", assumeYes))
            {
                return 1;
            }

            var newDek = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);

            Console.WriteLine("Re-encrypting all secrets under the new DEK (one transaction)...");
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);

            DekReEncryptCounts counts;
            try
            {
                counts = await DekRotationWalk.ReEncryptAllAsync(db, oldDek, newDek).ConfigureAwait(false);

                // Swap the wrapped DEK LAST (after all data is under the new DEK) so
                // every committed state keeps "wrapped-DEK matches the data's DEK".
                row.WrappedDek = DekProvider.Wrap(kek, newDek);
                row.RotatedUtc = DateTimeOffset.UtcNow;

                db.AuditEntries.Add(BuildAudit(AuditEventType.EncryptionDekRotated,
                    $"DEK rotated. Re-encrypted: {counts.Summary}", "rotate-dek"));

                await db.SaveChangesAsync().ConfigureAwait(false);
                await tx.CommitAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // A secret that the current DEK can't decrypt (wrong key, or a
                // corrupt / non-base64 ciphertext — base64 decode throws
                // FormatException, GCM tag mismatch throws CryptographicException).
                // The transaction rolls back on dispose, so nothing changed.
                await tx.RollbackAsync().ConfigureAwait(false);
                Console.Error.WriteLine(
                    "Rotation ABORTED — a stored secret could not be decrypted under the current DEK " +
                    $"({ex.GetType().Name}). No data was changed (transaction rolled back). This means a " +
                    "row is encrypted under a different key or is corrupt; the current Encryption:MasterKey " +
                    "must match the key the data was written under. Restore from backup if unsure.");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"DEK rotated. Re-encrypted {counts.Total} secrets under the new key:");
            Console.WriteLine($"  {counts.Summary}");
            return 0;
        }
    }

    // ── status ──────────────────────────────────────────────────────────────

    private static async Task<int> StatusAsync(string[] args, string contentRoot)
    {
        var builder = CliHost.CreateBuilder(contentRoot);
        if (RefuseIfMultiAccount(builder.Configuration)) { return 1; }
        var connectionString = builder.Configuration.GetConnectionString("KrakenDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("ConnectionStrings:KrakenDb is not configured.");
            return 1;
        }
        var hasKek = TryReadKek(builder.Configuration, out var kek, out _);

        builder.Services.AddKrakenDeployEncryption(
            hasKek ? Convert.ToBase64String(kek) : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.Services.AddKrakenDeployData(connectionString);
        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KrakenDbContext>>()
            .CreateDbContextAsync().ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await db.DataEncryptionKeys.AsNoTracking()
                .FirstOrDefaultAsync(k => k.AccountId == null).ConfigureAwait(false);
            if (row is null)
            {
                Console.WriteLine("DEK: NOT provisioned. Run 'database setup'.");
                return 0;
            }
            Console.WriteLine($"DEK: provisioned {row.CreatedUtc:u}"
                + (row.RotatedUtc is { } r ? $", last rotated {r:u}" : ", never rotated"));

            if (!hasKek)
            {
                Console.WriteLine("KEK: Encryption:MasterKey is NOT configured — cannot verify unwrap.");
                return 0;
            }
            try
            {
                DekProvider.Unwrap(kek, row.WrappedDek);
                Console.WriteLine("KEK: configured key correctly unwraps the DEK. [OK]");
            }
            catch (CryptographicException)
            {
                Console.WriteLine("KEK: configured Encryption:MasterKey does NOT unwrap the DEK. [MISMATCH]");
            }
            return 0;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Refuse the encryption verbs under a multi-account build — they
    /// bind to the shared KrakenDb, not a tenant DB, and per-account DEK is
    /// deferred. Mirrors the web host's boot fail-fast.</summary>
    private static bool RefuseIfMultiAccount(ConfigurationManager config)
    {
        if (config.GetValue("MultiAccount:Enabled", false))
        {
            Console.Error.WriteLine(
                "encryption commands are single-instance only. MultiAccount:Enabled uses a DB per " +
                "account; these verbs operate on the shared KrakenDb and per-account DEK rotation is " +
                "not yet implemented (M13.D.2). Refusing to avoid touching the wrong database.");
            return true;
        }
        return false;
    }

    private static bool TryReadKek(ConfigurationManager config, out byte[] kek, out string error)
    {
        kek = [];
        var masterKey = config["Encryption:MasterKey"];
        if (string.IsNullOrWhiteSpace(masterKey))
        {
            error = "Encryption:MasterKey (the KEK) is not configured — cannot rotate a key you don't have.";
            return false;
        }
        try
        {
            kek = Convert.FromBase64String(masterKey);
        }
        catch
        {
            error = "Encryption:MasterKey must be valid base64.";
            return false;
        }
        if (kek.Length != AesGcmCipher.KeyBytes)
        {
            error = $"Encryption:MasterKey must decode to {AesGcmCipher.KeyBytes} bytes (got {kek.Length}).";
            return false;
        }
        error = "";
        return true;
    }

    private static bool TryParseCommon(
        string[] args, out byte[]? newKey, out bool noBackup, out bool assumeYes, out string error,
        bool allowNewKey = true)
    {
        newKey = null;
        noBackup = false;
        assumeYes = false;
        error = "";
        for (var i = 0; i < args.Length; i++)
        {
            var flag = args[i];
            if (flag == "--new-key")
            {
                if (!allowNewKey) { error = "--new-key is not valid for this command."; return false; }
                if (i + 1 >= args.Length) { error = "--new-key requires a value."; return false; }
                var raw = args[++i];
                try
                {
                    newKey = Convert.FromBase64String(raw);
                }
                catch
                {
                    error = "--new-key must be valid base64.";
                    return false;
                }
                if (newKey.Length != AesGcmCipher.KeyBytes)
                {
                    error = $"--new-key must decode to {AesGcmCipher.KeyBytes} bytes (got {newKey.Length}).";
                    return false;
                }
            }
            else if (flag is "--no-backup") { noBackup = true; }
            else if (flag is "--yes" or "-y") { assumeYes = true; }
            else { error = $"Unknown option '{flag}'."; return false; }
        }
        return true;
    }

    /// <summary>Heuristic offline guard: a positive HTTP response from the
    /// anonymous <c>/healthz</c> means a server is live on that port; refuse.
    /// Connection-refused/timeout ⇒ not running. Unknown URL ⇒ don't block
    /// (the double-confirm still gates).</summary>
    private static async Task<bool> IsServerRunningAsync(ConfigurationManager config)
    {
        var raw = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';')[0]
                  ?? config["Urls"]?.Split(';')[0];
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
        {
            return false; // unknown bind URL — the double-confirm still gates.
        }

        // 0.0.0.0 / [::] / * are BIND-all addresses, never valid CONNECT targets —
        // probe loopback instead so a co-located running server is actually seen.
        var host = uri.Host is "0.0.0.0" or "*" or "[::]" or "::" ? "127.0.0.1" : uri.Host;
        var probeUrl = $"{uri.Scheme}://{host}:{uri.Port}/healthz";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await http.GetAsync(probeUrl).ConfigureAwait(false);
            return true; // anything answered ⇒ a server is live on that port.
        }
        catch
        {
            return false;
        }
    }

    private static int ServerRunningError()
    {
        Console.Error.WriteLine(
            "A running KrakenDeploy server was detected. Stop it before rotating keys — rotation is an "
            + "offline operation and a live server holds the old key in memory.");
        return 1;
    }

    private static async Task<bool> RunBackupAsync(AsyncServiceScope scope, bool noBackup)
    {
        if (noBackup)
        {
            Console.WriteLine("WARNING: --no-backup set. Rotating without a safety backup.");
            return true;
        }
        Console.WriteLine("Creating a safety backup before rotation...");
        var backupSvc = scope.ServiceProvider.GetRequiredService<BackupService>();
        var run = await backupSvc.RunOnceAsync("cli:encryption-rotate").ConfigureAwait(false);
        if (run.Outcome != KrakenDeploy.Server.Core.Domain.Backup.BackupOutcome.Success)
        {
            Console.Error.WriteLine($"Pre-rotation backup FAILED: {run.ErrorMessage}");
            Console.Error.WriteLine("Refusing to rotate without a backup. Re-run with --no-backup to override (NOT recommended).");
            return false;
        }
        Console.WriteLine($"Backup complete: {run.BundlePath}");
        return true;
    }

    private static bool ConfirmDestructive(string operation, string typedPhrase, bool assumeYes)
    {
        if (assumeYes) { return true; }
        Console.WriteLine();
        Console.WriteLine($"About to {operation}. Ensure the server is stopped and you have a backup.");
        Console.Write("Proceed? [y/N]: ");
        var first = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (first != "y" && first != "yes") { Console.WriteLine("Aborted."); return false; }
        Console.Write($"Type '{typedPhrase}' to confirm: ");
        var second = Console.ReadLine()?.Trim();
        if (!string.Equals(second, typedPhrase, StringComparison.Ordinal))
        {
            Console.WriteLine("Confirmation phrase did not match. Aborted.");
            return false;
        }
        return true;
    }

    private static AuditEntry BuildAudit(string eventType, string details, string verb) => new()
    {
        EventType   = eventType,
        SubjectType = "Encryption",
        Details     = details,
        OccurredUtc = DateTimeOffset.UtcNow,
        SpaceId     = null, // platform-wide event
        UserDisplay = $"cli:encryption {verb}",
    };

    private static int PrintTopLevelUsage(bool success = false)
    {
        Console.WriteLine("Usage: encryption <rotate-kek|rotate-dek|status> [options]");
        Console.WriteLine();
        Console.WriteLine("  rotate-kek [--new-key <base64-32>] [--no-backup] [--yes]");
        Console.WriteLine("      Re-wrap the DEK under a new KEK (no data walk). Refuses unless the");
        Console.WriteLine("      current Encryption:MasterKey unwraps the DEK. Prints the new KEK.");
        Console.WriteLine("  rotate-dek [--no-backup] [--yes]");
        Console.WriteLine("      Generate a new DEK and re-encrypt every secret in one transaction.");
        Console.WriteLine("  status");
        Console.WriteLine("      Report DEK presence + whether the configured KEK unwraps it.");
        Console.WriteLine();
        Console.WriteLine("  Both rotations are OFFLINE — stop the server first. A safety backup runs");
        Console.WriteLine("  before mutating unless --no-backup is given.");
        return success ? 0 : 1;
    }

    private static int UnknownSubcommand(string sub)
    {
        Console.Error.WriteLine($"Unknown encryption subcommand '{sub}'.");
        PrintTopLevelUsage();
        return 1;
    }
}
