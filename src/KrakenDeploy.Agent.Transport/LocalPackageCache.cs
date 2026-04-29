namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// Stores package zip files on the local filesystem under:
/// <c>{cacheRoot}/{packageId}/{version}/{packageId}.{version}.zip</c>
/// <para>
/// Thread-safe for concurrent reads; concurrent writes to the same
/// (packageId, version) are serialised at the OS level because
/// <see cref="FileMode.Create"/> truncates atomically.
/// </para>
/// </summary>
public sealed class LocalPackageCache(string cacheRoot) : IPackageCache
{
    // ── IPackageCache ─────────────────────────────────────────────────────────

    public string? TryGetCachedPath(string packageId, string version)
    {
        var path = BuildPath(packageId, version);
        return File.Exists(path) ? path : null;
    }

    public IReadOnlyList<string> GetCachedVersions(string packageId)
    {
        var packageDir = Path.Combine(cacheRoot, Sanitize(packageId));
        if (!Directory.Exists(packageDir))
        {
            return [];
        }

        var versions = new List<string>();
        foreach (var versionDir in Directory.EnumerateDirectories(packageDir))
        {
            var ver = Path.GetFileName(versionDir);
            if (File.Exists(BuildPath(packageId, ver)))
            {
                versions.Add(ver);
            }
        }

        return versions;
    }

    public async Task<string> StoreAsync(
        string packageId,
        string version,
        string sourcePath,
        CancellationToken ct = default)
    {
        var cachePath = BuildPath(packageId, version);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        await using var src = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        await using var dst = new FileStream(
            cachePath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        return cachePath;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string BuildPath(string packageId, string version)
        => Path.Combine(
            cacheRoot,
            Sanitize(packageId),
            Sanitize(version),
            $"{Sanitize(packageId)}.{Sanitize(version)}.zip");

    /// <summary>
    /// Replaces characters that are unsafe in filesystem path segments so that
    /// arbitrary packageId / version strings cannot escape the cache root.
    /// </summary>
    private static string Sanitize(string value)
        => value
            .Replace('/', '_').Replace('\\', '_').Replace(':', '_')
            .Replace('*', '_').Replace('?', '_').Replace('"', '_')
            .Replace('<', '_').Replace('>', '_').Replace('|', '_');
}
