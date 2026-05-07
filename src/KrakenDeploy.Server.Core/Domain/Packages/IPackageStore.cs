namespace KrakenDeploy.Server.Core.Domain.Packages;

/// <summary>
/// Abstraction over physical package file storage.
/// </summary>
public interface IPackageStore
{
    Task<string> StoreAsync(string packageId, string version, string fileName, Stream content, CancellationToken ct);
    Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct);
    string GetFullPath(string storedPath);
    Task DeleteAsync(string storedPath, CancellationToken ct);
}
