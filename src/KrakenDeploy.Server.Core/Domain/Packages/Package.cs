using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Packages;

/// <summary>
/// A versioned package file uploaded to the server and available for use in deployment steps.
/// </summary>
public class Package : Entity, ISpaceScoped
{
    public Guid SpaceId { get; set; }

    public required string PackageId { get; set; }
    public required string Version { get; set; }
    public required string FileName { get; set; }
    public required string StoredPath { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset UploadedUtc { get; set; }
}
