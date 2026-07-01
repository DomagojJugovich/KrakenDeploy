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
        // Sanitise the step name for use as a directory component.
        var safeStep = SanitiseName(stepName);
        var safeFile = SanitiseName(fileName);

        var dir = Path.Combine(RootPath, deploymentId.ToString("N"), safeStep);
        Directory.CreateDirectory(dir);

        var filePath   = Path.Combine(dir, safeFile);
        var storedPath = Path.Combine(deploymentId.ToString("N"), safeStep, safeFile);

        await using var fs = new FileStream(
            filePath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81_920, useAsync: true);
        await content.CopyToAsync(fs, ct).ConfigureAwait(false);

        return storedPath;
    }

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(RootPath, storedPath);
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
        var fullPath = Path.Combine(RootPath, storedPath);
        try { File.Delete(fullPath); } catch { /* best effort */ }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

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
