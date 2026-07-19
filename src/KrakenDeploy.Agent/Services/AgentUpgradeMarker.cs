using System.Text.Json;

namespace KrakenDeploy.Agent.Services;

/// <summary>
/// C6 — the "upgrade pending" marker written just before the agent exits to let
/// the supervisor relaunch the new binary. It survives the process boundary
/// (stored under the agent DATA directory, which is NOT swapped) and drives the
/// post-restart health gate: the freshly-started agent reads it, waits for the
/// new version to register healthy, and either commits (deletes the backup) or
/// rolls back (restores the backup) if the health gate times out.
/// <para>
/// Contains only paths and versions — never secrets.
/// </para>
/// </summary>
public sealed record AgentUpgradeMarker
{
    /// <summary>Version the agent was running BEFORE the swap.</summary>
    public required string FromVersion { get; init; }

    /// <summary>Version the swap installed (the one now on probation).</summary>
    public required string ToVersion { get; init; }

    /// <summary>The install directory that was swapped (the launch path).</summary>
    public required string InstallDir { get; init; }

    /// <summary>Where the PREVIOUS install was backed up (same volume as install).</summary>
    public required string BackupDir { get; init; }

    /// <summary>When the swap completed (UTC), for the health-gate deadline.</summary>
    public required DateTimeOffset WrittenUtc { get; init; }

    /// <summary>How long the new version has to register healthy before rollback.</summary>
    public required int HealthTimeoutSeconds { get; init; }

    /// <summary>
    /// The server contract version at swap time — the version the new build was
    /// expected to speak. Recorded for the rollback report / diagnostics.
    /// </summary>
    public required int ExpectedContractVersion { get; init; }

    /// <summary>
    /// C6 — how many times the new version has entered probation without ever
    /// confirming health. Incremented on each restart's probation pass and used to
    /// bound a crash-before-health-window loop (see
    /// <c>AgentUpdateConfig.MaxHealthAttempts</c>). Defaults to 0 (the swap that
    /// wrote the marker has not been probed yet).
    /// </summary>
    public int AttemptsUsed { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Reads the marker at <paramref name="path"/>, or null if it is absent or
    /// unreadable/corrupt (a corrupt marker is treated as "no pending upgrade"
    /// rather than crashing the boot — the swap either committed or never ran).
    /// </summary>
    public static AgentUpgradeMarker? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AgentUpgradeMarker>(
                File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the marker to <paramref name="path"/> (creating its directory)
    /// ATOMICALLY: it serialises to a sibling temp file and renames it over the
    /// target, so a process kill mid-write can never leave a truncated marker that
    /// <see cref="TryLoad"/> would discard (which would silently drop the health
    /// gate). The rename is atomic on the same volume.
    /// </summary>
    public static void Save(string path, AgentUpgradeMarker marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(marker, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Deletes the marker if present (best-effort).</summary>
    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            /* non-fatal */
        }
    }
}
