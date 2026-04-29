using System.IO.Compression;

namespace KrakenDeploy.Agent.Deployment;

/// <summary>
/// Extracts a zip package to a staging directory.
/// </summary>
public static class PackageExtractor
{
    /// <summary>
    /// Extracts <paramref name="zipFilePath"/> into
    /// <paramref name="destinationDirectory"/> (created if absent) and
    /// returns the destination directory path.
    /// </summary>
    public static Task<string> ExtractAsync(
        string zipFilePath,
        string destinationDirectory,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destinationDirectory);

        // ZipFile.ExtractToDirectory is synchronous; wrap in Task.Run so it
        // doesn't block the calling async context on large packages.
        return Task.Run(() =>
        {
            ZipFile.ExtractToDirectory(zipFilePath, destinationDirectory, overwriteFiles: true);
            return destinationDirectory;
        }, ct);
    }
}
