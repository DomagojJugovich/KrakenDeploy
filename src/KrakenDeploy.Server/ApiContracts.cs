namespace KrakenDeploy.Server;

// ── Space API ───────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /api/spaces.</summary>
public sealed record CreateSpaceRequest(string Slug, string Name, string? Description);

/// <summary>Request body for PUT /api/spaces/{id}.</summary>
public sealed record UpdateSpaceRequest(string Name, string? Description);

// ── Process API ────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /api/projects/{projectId}/process/steps.</summary>
/// <remarks>
/// <c>StepPackageName</c> + <c>StepPackageVersion</c> (Phase D-6) pair the
/// step to an installed step-package. Pass both, or omit both — when omitted,
/// the server auto-resolves to the highest installed package that claims
/// <paramref name="StepType"/>. Older clients that don't send either continue
/// to work via the auto-resolve path.
/// </remarks>
public sealed record AddStepRequest(
    string Name,
    string StepType,
    string PackageId,
    List<string> TargetRoles,
    Dictionary<string, string> Config,
    string? StepPackageName = null,
    string? StepPackageVersion = null);

// ── Step-package bulk upgrade (Phase D-10) ──────────────────────────────────

/// <summary>
/// Request body for <c>POST /api/step-packages/{name}/bulk-upgrade</c>.
/// Caller supplies the target version + the deployment-step and runbook-step
/// IDs to bump. Either list may be empty; both empty is a no-op that still
/// validates the target version exists.
/// </summary>
public sealed record BulkUpgradeRequest(
    string TargetVersion,
    List<Guid>? DeploymentStepIds = null,
    List<Guid>? RunbookStepIds    = null);

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
    DateTimeOffset? ScheduledFor = null,
    Core.Domain.Deployments.DeploymentFailureMode FailureMode
        = Core.Domain.Deployments.DeploymentFailureMode.BestEffort);

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

/// <summary>Request body for POST /api/step-templates/import-octopus-api.</summary>
public sealed record ImportOctopusApiRequest(string Json);

/// <summary>
/// Request body for POST /api/projects/{projectId}/process/import-octopus.
/// When <see cref="Replace"/> is <c>true</c>, existing steps on the project's
/// process are deleted before the imported steps are appended.
/// </summary>
public sealed record ImportDeploymentProcessRequest(string Json, bool Replace);

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
    Guid? TenantId = null,
    // D1 Phase 2 — parity with TriggerDeploymentRequest: a future ScheduledFor
    // holds the run Queued for the scheduled-dispatch job; AdditionalTargetIds
    // extends the target set (TargetId stays the canonical primary).
    DateTimeOffset? ScheduledFor = null,
    List<Guid>? AdditionalTargetIds = null);

// ── Tenant API ─────────────────────────────────────────────────────────────────

public sealed record CreateTenantRequest(string Name, string Slug, string? Description);

// ── Tag Sets API (extended tag sets — docs/extended-tag-sets-plan.md) ─────────

public sealed record CreateTagSetRequest(
    string Name,
    string? Description,
    KrakenDeploy.Server.Core.Domain.Tags.TagSetType Type
        = KrakenDeploy.Server.Core.Domain.Tags.TagSetType.MultiSelect,
    List<KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind>? Scopes = null,
    int SortOrder = 0);

public sealed record CreateTagRequest(string Name, string? Color, string? Description = null);

public sealed record ReorderTagsRequest(List<Guid>? OrderedTagIds);

/// <summary>PUT body for per-entity tag applications: <c>TagIds</c> for
/// select-type sets (empty list clears); <c>FreeTextValue</c> for free-text
/// sets (null/blank clears). Exactly one shape applies per set type.</summary>
public sealed record ApplyTagsRequest(List<Guid>? TagIds = null, string? FreeTextValue = null);

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
    Guid? ScopeTenantId = null,
    Guid? ScopeChannelId = null,
    Guid? ScopeProcessStepId = null);
