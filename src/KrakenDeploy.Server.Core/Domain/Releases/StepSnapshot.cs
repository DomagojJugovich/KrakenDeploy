namespace KrakenDeploy.Server.Core.Domain.Releases;

/// <summary>
/// Immutable snapshot of a deployment/runbook step taken at release or run creation time.
/// Stored as jsonb so historical records remain accurate after process edits.
/// </summary>
public sealed class StepSnapshot
{
    public string Name { get; init; } = string.Empty;
    public string StepType { get; init; } = string.Empty;
    public string PackageId { get; init; } = string.Empty;
    public string PackageVersion { get; init; } = string.Empty;
    public List<string> TargetRoles { get; init; } = [];
    public Dictionary<string, string> Config { get; init; } = [];
    public int SortOrder { get; init; }
}
