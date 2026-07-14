using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Packages;

namespace KrakenDeploy.Server.Data.Storage;

/// <summary>
/// Stores package files on the local filesystem. Single-instance:
/// <c>{DataPath}/packages/{packageId}/{version}/{fileName}</c>. Multi-account: namespaced
/// by the active account so no two tenants share a file tree —
/// <c>{DataPath}/accounts/{accountId}/packages/…</c>. The account id (not the subdomain)
/// keys the path so a subdomain rename never orphans a stored file.
/// </summary>
public sealed class LocalPackageStore(string dataPath, IAccountContext accountContext) : IPackageStore
{
    private string RootPath => accountContext.IsResolved
        ? Path.Combine(dataPath, "accounts", accountContext.CurrentAccountId.ToString(), "packages")
        : Path.Combine(dataPath, "packages");

    public async Task<string> StoreAsync(
        string packageId, string version, string fileName,
        Stream content, CancellationToken ct)
    {
        var filePath = ResolveWithinRoot(Path.Combine(packageId, version, fileName));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        await using var fs = new FileStream(
            filePath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await content.CopyToAsync(fs, ct).ConfigureAwait(false);

        // Stored path is always relative to RootPath and uses forward slashes
        // for cross-platform compatibility in the DB value.
        return $"{packageId}/{version}/{fileName}";
    }

    public Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct)
    {
        var fullPath = GetFullPath(storedPath);
        Stream stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public string GetFullPath(string storedPath)
        => ResolveWithinRoot(storedPath.Replace('/', Path.DirectorySeparatorChar));

    public Task DeleteAsync(string storedPath, CancellationToken ct)
    {
        var fullPath = GetFullPath(storedPath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves <paramref name="relative"/> against <see cref="RootPath"/> and asserts
    /// the result stays strictly under the root — defence in depth against a path
    /// component that escapes the package tree (the caller already sanitizes, but a
    /// store must never write/read/delete outside its root). Covers the multi-account
    /// <c>accounts/{id}/packages</c> root too.
    /// </summary>
    private string ResolveWithinRoot(string relative)
    {
        var root = Path.GetFullPath(RootPath);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Resolved package path escapes the package storage root.");
        }
        return full;
    }
}
