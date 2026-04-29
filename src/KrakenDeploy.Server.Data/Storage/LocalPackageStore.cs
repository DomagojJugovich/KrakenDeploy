using KrakenDeploy.Server.Core.Domain.Packages;

namespace KrakenDeploy.Server.Data.Storage;

/// <summary>
/// Stores package files on the local filesystem under
/// <c>{DataPath}/packages/{packageId}/{version}/{fileName}</c>.
/// </summary>
public sealed class LocalPackageStore(string dataPath) : IPackageStore
{
    private string RootPath => Path.Combine(dataPath, "packages");

    public async Task<string> StoreAsync(
        string packageId, string version, string fileName,
        Stream content, CancellationToken ct)
    {
        var dir = Path.Combine(RootPath, packageId, version);
        Directory.CreateDirectory(dir);

        var filePath = Path.Combine(dir, fileName);
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
        => Path.Combine(RootPath, storedPath.Replace('/', Path.DirectorySeparatorChar));

    public Task DeleteAsync(string storedPath, CancellationToken ct)
    {
        var fullPath = GetFullPath(storedPath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
        return Task.CompletedTask;
    }
}
