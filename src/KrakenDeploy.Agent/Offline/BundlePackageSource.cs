using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts.Offline;

namespace KrakenDeploy.Agent.Offline;

/// <summary>
/// Offline <see cref="IPackageSource"/>: materialises a deployable package by
/// copying it out of the bundle's <c>packages/{id}/{version}/</c> directory
/// (no network, no cache). Mirrors the gRPC downloader's staging-copy result.
/// </summary>
public sealed class BundlePackageSource(string bundleRoot) : IPackageSource
{
    public Task<string> DownloadAsync(
        string packageId, string version, string destDirectory, CancellationToken ct)
    {
        var dir = Path.Combine(
            bundleRoot, OfflineBundleLayout.PackagesDir, packageId, version);
        if (!Directory.Exists(dir))
        {
            throw new FileNotFoundException(
                $"Offline bundle is missing package {packageId} v{version} (expected at '{dir}').");
        }

        var src = Directory.EnumerateFiles(dir).FirstOrDefault()
            ?? throw new FileNotFoundException(
                $"Offline bundle directory for {packageId} v{version} contains no package file.");

        Directory.CreateDirectory(destDirectory);
        var dest = Path.Combine(destDirectory, Path.GetFileName(src));
        File.Copy(src, dest, overwrite: true);
        return Task.FromResult(dest);
    }
}
