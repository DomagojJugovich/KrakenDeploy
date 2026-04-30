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

// ── Step-template API ──────────────────────────────────────────────────────────

/// <summary>Request body for POST /api/step-templates.</summary>
public sealed record CreateStepTemplateRequest(
    string Name,
    string ActionType,
    string? Description,
    Dictionary<string, string>? Properties,
    List<StepTemplateParameterRequest>? Parameters);

/// <summary>Request body for PUT /api/step-templates/{id:guid}.</summary>
public sealed record UpdateStepTemplateRequest(
    string Name,
    string? Description,
    Dictionary<string, string>? Properties,
    List<StepTemplateParameterRequest>? Parameters);

/// <summary>Request body for POST /api/step-templates/import.</summary>
public sealed record ImportStepTemplateRequest(
    string Json,
    string? ImportSource);

/// <summary>Parameter definition within a step-template create/update request.</summary>
public sealed record StepTemplateParameterRequest(
    string Name,
    string Label,
    string? HelpText,
    string? DefaultValue,
    string ControlType,
    List<string>? SelectOptions);

// ── Variable API ───────────────────────────────────────────────────────────────

/// <summary>
/// Request body for POST /api/projects/{projectId}/variables and
/// PUT /api/projects/{projectId}/variables/{variableId}.
/// </summary>
public sealed record UpsertVariableRequest(
    string Name,
    string Value,
    /// <summary>"String", "Sensitive", or "StringArray".</summary>
    string Type,
    Guid? ScopeEnvironmentId,
    Guid? ScopeTargetId,
    List<string>? ScopeRoles);
