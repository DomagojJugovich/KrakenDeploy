namespace KrakenDeploy.Server;

// ── Process API ────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /api/projects/{projectId}/process/steps.</summary>
public sealed record AddStepRequest(
    string Name,
    string StepType,
    string PackageId,
    List<string> TargetRoles,
    Dictionary<string, string> Config);

// ── Release API ────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /api/projects/{projectId}/releases.</summary>
public sealed record CreateReleaseRequest(
    string Version,
    IReadOnlyDictionary<string, string>? PackageVersions,
    string? ReleaseNotes);

// ── Deployment API ─────────────────────────────────────────────────────────────

/// <summary>Request body for POST /api/deployments.</summary>
public sealed record TriggerDeploymentRequest(
    Guid ReleaseId,
    Guid EnvironmentId,
    Guid TargetId);
