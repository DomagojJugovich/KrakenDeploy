using KrakenDeploy.Server.Core.Domain.Accounts;

namespace KrakenDeploy.Server.Data.ArtifactStorage;

/// <summary>
/// Stores deployment artifact files on the local filesystem. Single-instance:
/// <c>{dataPath}/artifacts/{deploymentId}/{stepName}/{fileName}</c>. Multi-account:
/// namespaced by the active account so no two tenants share a file tree —
/// <c>{dataPath}/accounts/{accountId}/artifacts/…</c>. The account id (not the subdomain)
/// keys the path so a subdomain rename never orphans a stored file.
/// </summary>
public sealed class LocalArtifactStore(string dataPath, IAccountContext accountContext) : IArtifactStore
{
    private string RootPath => accountContext.IsResolved
        ? Path.Combine(dataPath, "accounts", accountContext.CurrentAccountId.ToString(), "artifacts")
        : Path.Combine(dataPath, "artifacts");

    /// <inheritdoc/>
    public async Task<string> SaveAsync(
        Guid deploymentId,
        string stepName,
        string fileName,
        Stream content,
        CancellationToken ct = default)
    {
        // Sanitise the step name + file name for use as path components.
        var safeStep = SanitiseName(stepName);
        var safeFile = SanitiseName(fileName);

        var relative   = Path.Combine(deploymentId.ToString("N"), safeStep, safeFile);
        // Defence in depth (T0-5 parity with LocalPackageStore): the names are
        // already sanitised, but a store must never write outside its root — so
        // assert containment on the resolved path before creating anything.
        var filePath   = ResolveWithinRoot(relative);
        var storedPath = relative;

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        await using var fs = new FileStream(
            filePath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81_920, useAsync: true);
        await content.CopyToAsync(fs, ct).ConfigureAwait(false);

        return storedPath;
    }

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct = default)
    {
        var fullPath = ResolveWithinRoot(storedPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Artifact file not found.", fullPath);
        }

        Stream stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81_920, useAsync: true);
        return Task.FromResult(stream);
    }

    /// <inheritdoc/>
    public void Delete(string storedPath)
    {
        var fullPath = ResolveWithinRoot(storedPath);
        try { File.Delete(fullPath); } catch { /* best effort */ }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves <paramref name="relative"/> against <see cref="RootPath"/> and asserts
    /// the result stays strictly under the root. Mirrors
    /// <c>LocalPackageStore.ResolveWithinRoot</c> — the read/delete inputs are
    /// server-constructed from sanitised parts today (no injection route), but a
    /// store must never read/write/delete outside its root regardless of how the
    /// path was produced. Covers the multi-account <c>accounts/{id}/artifacts</c>
    /// root too.
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
                "Resolved artifact path escapes the artifact storage root.");
        }
        return full;
    }

    private static string SanitiseName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars   = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
