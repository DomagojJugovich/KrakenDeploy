using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KrakenDeploy.Agent.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Identity;

/// <summary>
/// Loads and saves the agent identity to <c>agent.json</c> in the configured data
/// directory. The file holds the long-lived bearer token, so it is protected at
/// rest (A8/T1-12):
/// <list type="bullet">
///   <item>Windows — DPAPI (LocalMachine scope) encrypts the content, and the
///   data directory's ACL is tightened via <c>icacls</c> to SYSTEM +
///   Administrators + the service account (LocalMachine DPAPI is decryptable by
///   any local process, so the ACL is the on-box confidentiality control).</item>
///   <item>Unix — owner-only (chmod 600) file permissions, as before.</item>
/// </list>
/// A plaintext file written by an older agent is auto-migrated to the protected
/// form on first read (Windows).
/// </summary>
public sealed class AgentIdentityStore(
    IOptions<AgentConfig> agentConfig,
    ILogger<AgentIdentityStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    private const string FileName = "agent.json";

    // Prefix that marks a DPAPI-protected file, so TryLoad can distinguish it from
    // a legacy plaintext-JSON file (which starts with '{').
    private static readonly byte[] DpapiMagic = "KDPAPIv1"u8.ToArray();

    private string IdentityFilePath =>
        Path.Combine(agentConfig.Value.ResolvedDataPath, FileName);

    // ── Load ───────────────────────────────────────────────────────────────

    public async Task<AgentIdentity?> TryLoadAsync(CancellationToken ct)
    {
        var path = IdentityFilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        var raw = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);

        if (HasDpapiMagic(raw))
        {
            if (!OperatingSystem.IsWindows())
            {
                logger.LogError(
                    "agent.json is DPAPI-protected (written on Windows) but this host is not " +
                    "Windows — it cannot be decrypted. Re-enroll the agent on this host.");
                return null;
            }

            try
            {
                var json = UnprotectToJson(raw);
                return JsonSerializer.Deserialize<AgentIdentity>(json);
            }
            catch (CryptographicException ex)
            {
                // e.g. a LocalMachine blob copied from another machine, or corruption.
                // Fail closed to "unenrolled" so the agent re-registers cleanly.
                logger.LogError(ex,
                    "Failed to decrypt agent.json; treating the agent as unenrolled. Re-enroll it.");
                return null;
            }
        }

        // Legacy plaintext (older enrollment) or a Unix plaintext file.
        var identity = JsonSerializer.Deserialize<AgentIdentity>(Encoding.UTF8.GetString(raw));
        if (identity is not null && OperatingSystem.IsWindows())
        {
            logger.LogInformation(
                "Migrating plaintext agent.json to DPAPI-protected form.");
            try
            {
                await SaveAsync(identity, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to migrate agent.json to protected form; the token remains in plaintext.");
            }
        }

        return identity;
    }

    // ── Save ───────────────────────────────────────────────────────────────

    public async Task SaveAsync(AgentIdentity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var dir = agentConfig.Value.ResolvedDataPath;
        Directory.CreateDirectory(dir);

        var path = IdentityFilePath;
        var json = JsonSerializer.Serialize(identity, JsonOptions);
        var tmp = path + ".tmp";

        if (OperatingSystem.IsWindows())
        {
            HardenWindowsDirectory(dir);
            await File.WriteAllBytesAsync(tmp, Protect(json), ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            return;
        }

        await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool HasDpapiMagic(byte[] raw) =>
        raw.Length >= DpapiMagic.Length &&
        raw.AsSpan(0, DpapiMagic.Length).SequenceEqual(DpapiMagic);

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(string json)
    {
        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json), optionalEntropy: null, DataProtectionScope.LocalMachine);
        var output = new byte[DpapiMagic.Length + cipher.Length];
        Buffer.BlockCopy(DpapiMagic, 0, output, 0, DpapiMagic.Length);
        Buffer.BlockCopy(cipher, 0, output, DpapiMagic.Length, cipher.Length);
        return output;
    }

    [SupportedOSPlatform("windows")]
    private static string UnprotectToJson(byte[] raw)
    {
        var cipher = raw[DpapiMagic.Length..];
        var plain = ProtectedData.Unprotect(
            cipher, optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(plain);
    }

    [SupportedOSPlatform("windows")]
    private void HardenWindowsDirectory(string dir)
    {
        // Remove inherited ACEs (e.g. %ProgramData% grants Users read) and grant
        // only SYSTEM + Administrators + the current (service) account. Well-known
        // SIDs (not localized group names) keep this correct on non-English Windows.
        // Best-effort: log loudly on failure rather than fail enrollment — the file
        // is still DPAPI-encrypted, though on-box confidentiality then rests on the
        // pre-existing directory ACL.
        var account = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var args = new[]
        {
            dir,
            "/inheritance:r",
            "/grant:r", "*S-1-5-18:(OI)(CI)F",     // NT AUTHORITY\SYSTEM
            "/grant:r", "*S-1-5-32-544:(OI)(CI)F", // BUILTIN\Administrators
            "/grant:r", $"{account}:(OI)(CI)F",
        };

        try
        {
            var psi = new ProcessStartInfo("icacls")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                logger.LogWarning("Could not start icacls to harden the agent data directory {Dir}.", dir);
                return;
            }

            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10_000);
            if (proc.ExitCode != 0)
            {
                logger.LogWarning(
                    "icacls hardening of {Dir} exited {Code}: {Error}",
                    dir, proc.ExitCode, stderr.Trim());
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to harden the agent data directory {Dir} via icacls; agent.json remains " +
                "DPAPI-encrypted but the directory ACL was not tightened.", dir);
        }
    }
}
