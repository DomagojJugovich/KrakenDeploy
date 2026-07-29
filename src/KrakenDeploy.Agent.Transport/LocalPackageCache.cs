using System.Collections.Concurrent;

namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// Stores package zip files on the local filesystem under:
/// <c>{cacheRoot}/{packageId}/{version}/{packageId}.{version}.zip</c>
/// <para>
/// B7 — safe under concurrency and crashes. A cache file only ever EXISTS at
/// its final path when it is complete: <see cref="StoreAsync"/> copies into a
/// unique <c>.tmp-*</c> sibling and atomically renames it into place (same
/// directory, same volume), so <see cref="TryGetCachedPath"/>'s existence
/// check doubles as the completion marker. Pre-B7 the store wrote the final
/// path directly with a truncating <c>FileMode.Create</c>: a concurrent
/// reader saw a torn zip, and an agent crash mid-copy left a permanently
/// poisoned cache entry that every later hit returned. Content integrity is
/// the DOWNLOADER's job — it SHA-256-verifies the bytes before ever calling
/// <see cref="StoreAsync"/> (delta transfers are verified by Octodiff), so
/// re-hashing on every cache hit would buy nothing.
/// </para>
/// <para>
/// Concurrent stores of the same (packageId, version) are single-flighted by
/// a per-key gate, and a store finding the entry already present SKIPS the
/// copy — entries are content-addressed by (packageId, version), so the
/// existing verified bytes are the same bytes. That skip also prevents a
/// rename onto a zip a concurrent extraction has open (Windows refuses to
/// replace an open file).
/// </para>
/// </summary>
public sealed class LocalPackageCache(string cacheRoot) : IPackageCache
{
    // Per-(packageId, version) store gates. Bounded by the number of distinct
    // packages an agent ever caches — no eviction needed.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _storeGates =
        new(StringComparer.OrdinalIgnoreCase);

    // ── IPackageCache ─────────────────────────────────────────────────────────

    public string? TryGetCachedPath(string packageId, string version)
    {
        var path = BuildPath(packageId, version);
        // Existence == completeness: the file is only ever renamed into place
        // whole (see StoreAsync). In-progress .tmp-* siblings never match.
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

        versions.Sort(VersionComparer.Instance);
        return versions;
    }

    public async Task<string> StoreAsync(
        string packageId,
        string version,
        string sourcePath,
        CancellationToken ct = default)
    {
        var cachePath = BuildPath(packageId, version);
        var gate = _storeGates.GetOrAdd(
            cachePath, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Already cached (a concurrent store won the race, or a redundant
            // re-store of a version the hit check missed) — the existing entry
            // holds the same verified bytes; don't rewrite a file an
            // extraction may have open.
            if (File.Exists(cachePath))
            {
                return cachePath;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

            // Unique temp in the SAME directory so the rename is an atomic
            // same-volume move. A crash before the move leaves only orphaned
            // .tmp-* files, never a half-written entry at the final path.
            var tempPath = $"{cachePath}.tmp-{Guid.NewGuid():N}";
            try
            {
                await using (var src = new FileStream(
                    sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 81920, useAsync: true))
                await using (var dst = new FileStream(
                    tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, useAsync: true))
                {
                    await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                }

                File.Move(tempPath, cachePath);
            }
            catch
            {
                try { File.Delete(tempPath); } catch (IOException) { }
                throw;
            }

            return cachePath;
        }
        finally
        {
            gate.Release();
        }
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

    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null && y is null) { return 0; }
            if (x is null) { return -1; }
            if (y is null) { return 1; }
            if (Version.TryParse(x, out var vx) && Version.TryParse(y, out var vy))
            {
                return vx.CompareTo(vy);
            }
            return string.CompareOrdinal(x, y);
        }
    }
}
