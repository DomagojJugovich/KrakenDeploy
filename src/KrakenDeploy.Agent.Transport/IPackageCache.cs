namespace KrakenDeploy.Agent.Transport;

/// <summary>
/// Local package file cache on the agent.
/// Avoids redundant full-package downloads when the same (packageId, version) is
/// deployed repeatedly to the same agent, and provides the basis file for Octodiff
/// delta transfers.
/// </summary>
public interface IPackageCache
{
    /// <summary>
    /// Returns the full filesystem path of the cached zip for the given
    /// <paramref name="packageId"/> / <paramref name="version"/>, or
    /// <c>null</c> if no cache entry exists.
    /// </summary>
    string? TryGetCachedPath(string packageId, string version);

    /// <summary>
    /// Returns all versions of <paramref name="packageId"/> present in the local cache.
    /// Used to select a delta-basis version when requesting a new version from the server.
    /// </summary>
    IReadOnlyList<string> GetCachedVersions(string packageId);

    /// <summary>
    /// Copies <paramref name="sourcePath"/> into the cache and returns the cached file path.
    /// Idempotent: if an entry already exists it is overwritten.
    /// </summary>
    Task<string> StoreAsync(
        string packageId,
        string version,
        string sourcePath,
        CancellationToken ct = default);
}
