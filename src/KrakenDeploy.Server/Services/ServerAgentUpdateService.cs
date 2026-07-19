using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KrakenDeploy.Contracts;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Services;

/// <summary>
/// Reads the <c>version.json</c> manifest from the agent binaries directory
/// and serves the latest version info per runtime identifier (RID).
/// </summary>
public sealed class ServerAgentUpdateService
{
    private readonly string _binariesPath;
    private readonly ILogger<ServerAgentUpdateService> _logger;

    // C6: cache the computed archive hash per (path, length, last-write) so a
    // fleet of agents polling update-info every few minutes does not re-hash the
    // (tens-of-MB) archive on every request. The binaries directory is a
    // server-global artifact shared across all accounts, so a process-wide cache
    // is correct here (it holds no tenant data). Invalidated automatically when
    // the operator replaces the file (length or mtime changes).
    private readonly ConcurrentDictionary<string, string> _shaCache = new();

    public ServerAgentUpdateService(
        IOptions<AgentUpdateSettings> settings,
        IConfiguration config,
        ILogger<ServerAgentUpdateService> logger)
    {
        var configured = settings.Value.BinariesPath;
        _binariesPath = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(config["Server:DataPath"] ?? "data", configured);
        _logger = logger;
    }

    /// <summary>
    /// C6 — the single source of truth for the update-info response. Resolves
    /// the manifest, decides whether <paramref name="currentVersion"/> is behind
    /// <paramref name="rid"/>'s published version, and — when it is — attaches a
    /// MANDATORY server-computed SHA-256 plus both contract versions so the agent
    /// can verify the download and refuse a contract-skewed build. Returns a
    /// no-update result (never throws) for any missing-manifest / missing-file /
    /// unverifiable case, so a misconfigured server degrades to "no update"
    /// rather than shipping an unverifiable binary.
    /// </summary>
    public AgentUpdateInfo GetUpdateInfo(string rid, string currentVersion)
    {
        var manifest = GetManifest();
        if (manifest?.Rids is null || !manifest.Rids.TryGetValue(rid, out var ridInfo))
        {
            return NoUpdate;
        }

        var updateAvailable = !string.Equals(
            currentVersion, ridInfo.Version, StringComparison.OrdinalIgnoreCase);
        if (!updateAvailable)
        {
            return new AgentUpdateInfo(
                UpdateAvailable: false,
                LatestVersion: ridInfo.Version,
                DownloadUrl: null,
                SizeBytes: null,
                Sha256: null,
                ServerContractVersion: AgentContract.CurrentVersion,
                TargetContractVersion: null);
        }

        // An update is available — the hash is mandatory. Compute it from the
        // actual file (not the manifest's defaultable field); if the file is
        // missing we cannot vouch for a download, so we do NOT offer the update.
        var sha = ComputeSha256(rid);
        if (string.IsNullOrWhiteSpace(sha))
        {
            _logger.LogWarning(
                "Agent update for RID {Rid} is published ({Version}) but its binary " +
                "could not be hashed — refusing to offer an unverifiable update.",
                rid, ridInfo.Version);
            return NoUpdate;
        }

        return new AgentUpdateInfo(
            UpdateAvailable: true,
            LatestVersion: ridInfo.Version,
            DownloadUrl: $"/api/agents/download/{rid}",
            SizeBytes: ridInfo.SizeBytes,
            Sha256: sha,
            ServerContractVersion: AgentContract.CurrentVersion,
            TargetContractVersion: ridInfo.ContractVersion);
    }

    private static readonly AgentUpdateInfo NoUpdate = new(
        UpdateAvailable: false,
        LatestVersion: null,
        DownloadUrl: null,
        SizeBytes: null,
        Sha256: null,
        ServerContractVersion: AgentContract.CurrentVersion,
        TargetContractVersion: null);

    /// <summary>
    /// Returns update info for the given RID, or null if the manifest or RID is missing.
    /// </summary>
    public AgentUpdateManifest? GetManifest()
    {
        var manifestPath = Path.Combine(_binariesPath, "version.json");
        if (!File.Exists(manifestPath))
        {
            _logger.LogDebug("No agent version manifest at {Path}.", manifestPath);
            return null;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<AgentUpdateManifest>(json);
            if (manifest is null)
            {
                _logger.LogWarning("Agent version manifest at {Path} is empty or invalid.", manifestPath);
                return null;
            }

            return manifest;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read agent version manifest at {Path}.", manifestPath);
            return null;
        }
    }

    /// <summary>
    /// Opens the agent binary file for the given RID. Returns null if not found.
    /// </summary>
    public (Stream Stream, string FileName, string ContentType)? OpenDownload(string rid)
    {
        var manifest = GetManifest();
        if (manifest?.Rids is null || !manifest.Rids.TryGetValue(rid, out var info))
        {
            return null;
        }

        var filePath = Path.Combine(_binariesPath, info.FileName);
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Agent binary for RID {Rid} not found at {Path}.", rid, filePath);
            return null;
        }

        var contentType = rid.StartsWith("win", StringComparison.OrdinalIgnoreCase)
            ? "application/zip"
            : "application/gzip";

        return (new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read),
            info.FileName, contentType);
    }

    /// <summary>
    /// Computes the SHA256 hash of the agent binary for the given RID.
    /// Returns null if the file is not found. Cached per (path, length,
    /// last-write) so repeated polls do not re-hash an unchanged archive.
    /// </summary>
    public string? ComputeSha256(string rid)
    {
        var manifest = GetManifest();
        if (manifest?.Rids is null || !manifest.Rids.TryGetValue(rid, out var info))
        {
            return null;
        }

        var filePath = Path.Combine(_binariesPath, info.FileName);
        var fi = new FileInfo(filePath);
        if (!fi.Exists)
        {
            return null;
        }

        var cacheKey = $"{filePath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        return _shaCache.GetOrAdd(cacheKey, _ =>
        {
            using var stream = File.OpenRead(filePath);
            var sha = SHA256.HashData(stream);
            return Convert.ToHexStringLower(sha);
        });
    }
}

/// <summary>
/// Deserialised from <c>version.json</c> in the agent binaries directory.
/// </summary>
public sealed class AgentUpdateManifest
{
    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; set; } = "";

    [JsonPropertyName("rids")]
    public Dictionary<string, AgentRidInfo> Rids { get; set; } = [];
}

public sealed class AgentRidInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    /// <summary>
    /// C6: no longer trusted for verification — the server computes the hash from
    /// the actual file (<see cref="ServerAgentUpdateService.ComputeSha256"/>) and
    /// serves THAT. Retained so an operator can record it for their own audit,
    /// but the update-info response never echoes this field.
    /// </summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    /// <summary>
    /// C6 — the agent wire-contract version (<see cref="AgentContract.CurrentVersion"/>)
    /// the published build for this RID speaks. The operator declares it when
    /// publishing the manifest; the update-info endpoint advertises it so the
    /// agent can refuse a build whose contract version does not match the running
    /// server's. Defaults to 0 (an unknown/undeclared build) — which never
    /// matches the server's current version, so an undeclared build is refused
    /// rather than silently applied.
    /// </summary>
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; set; }
}
