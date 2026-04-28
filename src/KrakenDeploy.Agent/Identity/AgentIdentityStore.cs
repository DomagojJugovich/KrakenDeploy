using System.Text.Json;
using KrakenDeploy.Agent.Config;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Identity;

/// <summary>
/// Loads and saves the agent identity to <c>agent.json</c> in the configured data directory.
/// On Unix the file is created with owner-only (600) permissions to protect the bearer token.
/// </summary>
public sealed class AgentIdentityStore(IOptions<AgentConfig> agentConfig)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    private const string FileName = "agent.json";

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

        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<AgentIdentity>(json);
    }

    // ── Save ───────────────────────────────────────────────────────────────

    public async Task SaveAsync(AgentIdentity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var dir = agentConfig.Value.ResolvedDataPath;
        Directory.CreateDirectory(dir);

        var path = IdentityFilePath;
        var json = JsonSerializer.Serialize(identity, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);

        // Protect the bearer token: owner read/write only (chmod 600) on Unix.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
