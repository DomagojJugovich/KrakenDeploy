using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// Returns null if the file is not found.
    /// </summary>
    public string? ComputeSha256(string rid)
    {
        var manifest = GetManifest();
        if (manifest?.Rids is null || !manifest.Rids.TryGetValue(rid, out var info))
        {
            return null;
        }

        var filePath = Path.Combine(_binariesPath, info.FileName);
        if (!File.Exists(filePath))
        {
            return null;
        }

        var sha = SHA256.HashData(File.ReadAllBytes(filePath));
        return Convert.ToHexStringLower(sha);
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

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}
