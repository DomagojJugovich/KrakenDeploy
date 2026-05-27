namespace KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;

/// <summary>
/// LLM-shaped view of a deployment / runbook process (M11.B). Slim by
/// design — per-step <see cref="ProcessStepContextDto.ConfigSummary"/>
/// carries the 3-5 high-signal keys from the curator, not the full Config
/// blob. The AI drills into the full Config via
/// <see cref="ProcessStepContextDto.FullConfigUri"/> only when a specific
/// step needs deeper inspection.
/// <para>
/// Consumed by both the MCP <c>kraken://.../process</c> Resource (gated +
/// audited) and the M11.C diagnosis context assembler (system job, no
/// gate). Same shape for live processes (<c>DeploymentStep</c>) and frozen
/// release snapshots (<c>StepSnapshot</c>).
/// </para>
/// </summary>
public sealed record ProcessContextDto(
    string ProjectName,
    string? ReleaseVersion,
    IReadOnlyList<ProcessStepContextDto> Steps);

/// <summary>One step in a <see cref="ProcessContextDto"/>.</summary>
public sealed record ProcessStepContextDto(
    int Index,
    string Name,
    string StepType,
    IReadOnlyList<string> TargetRoles,
    bool Required,
    bool IsServerSide,
    string StartTrigger,
    string? ParentName,
    IReadOnlyDictionary<string, string> ConfigSummary,
    string FullConfigUri);
