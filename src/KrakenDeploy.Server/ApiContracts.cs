namespace KrakenDeploy.Server;

// ── Space API ───────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /api/spaces.</summary>
public sealed record CreateSpaceRequest(string Slug, string Name, string? Description);

/// <summary>Request body for PUT /api/spaces/{id}.</summary>
public sealed record UpdateSpaceRequest(string Name, string? Description);

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
    string? ReleaseNotes,
    Guid? ChannelId = null);

// ── Deployment API ─────────────────────────────────────────────────────────────

/// <summary>Request body for POST /api/deployments.</summary>
public sealed record TriggerDeploymentRequest(
    Guid ReleaseId,
    Guid EnvironmentId,
    Guid TargetId,
    Guid? TenantId = null,
    DateTimeOffset? ScheduledFor = null);

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

/// <summary>Request body for POST /api/step-templates/import-folder.</summary>
public sealed record ImportFolderRequest(string FolderPath);

/// <summary>Parameter definition within a step-template create/update request.</summary>
public sealed record StepTemplateParameterRequest(
    string Name,
    string Label,
    string? HelpText,
    string? DefaultValue,
    string ControlType,
    List<string>? SelectOptions);

// ── Lifecycle API ──────────────────────────────────────────────────────────────

public sealed record CreateLifecycleRequest(string Name, string? Description);

public sealed record UpdateLifecycleRequest(
    string Name,
    string? Description,
    List<KrakenDeploy.Server.Core.Domain.Lifecycles.LifecyclePhase> Phases);

// ── Channel API ────────────────────────────────────────────────────────────────

public sealed record UpsertChannelRequest(
    string Name,
    bool IsDefault = false,
    Guid? LifecycleId = null,
    string? VersionRange = null,
    string? VersionTag = null);

// ── Runbook API ────────────────────────────────────────────────────────────────

public sealed record CreateRunbookRequest(string Name, string? Description);

public sealed record TriggerRunbookRunRequest(
    Guid EnvironmentId,
    Guid TargetId,
    Guid? TenantId = null);

// ── Tenant API ─────────────────────────────────────────────────────────────────

public sealed record CreateTenantRequest(string Name, string Slug, string? Description);

public sealed record CreateTagSetRequest(string Name, string? Description, int SortOrder = 0);

public sealed record CreateTenantTagRequest(string Name, string? Color);

// ── Offline Drop API ──────────────────────────────────────────────────────

/// <summary>Request body for POST /api/targets/{id}/offline-drop-config.</summary>
public sealed record SaveOfflineDropConfigRequest(
    KrakenDeploy.Server.Core.Domain.Targets.OfflineDropDeliveryChannel DeliveryChannel,
    string? SmtpHost = null,
    int SmtpPort = 587,
    bool SmtpUseSsl = true,
    string? SmtpUsername = null,
    string? SmtpPassword = null,
    string? SmtpRecipient = null,
    string? SmtpSender = null,
    string? WebhookUrl = null,
    string? WebhookSecret = null,
    string? FileSharePath = null,
    string? FileShareUsername = null,
    string? FileSharePassword = null);

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
    List<string>? ScopeRoles,
    Guid? ScopeTenantId = null);
