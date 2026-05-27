using KrakenDeploy.Server.Core.Domain.Deployments;

namespace KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;

/// <summary>
/// LLM-shaped summary of a deployment (M11.B). Slim — metadata only, no
/// nested log / artifact / outcome collections. The AI pulls the log via
/// the <c>kraken://deployments/{id}/log</c> resource or the
/// <c>get_deployment_log</c> tool when it needs detail.
/// </summary>
public sealed record DeploymentSummaryDto(
    Guid Id,
    string ProjectName,
    string ProjectSlug,
    string ReleaseVersion,
    string EnvironmentName,
    IReadOnlyList<string> TargetNames,
    DeploymentStatus Status,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc);

/// <summary>A deployment summary plus the tail of its log — what
/// <c>get_deployment_log</c> returns when the caller wants context without
/// the full ndjson stream.</summary>
public sealed record DeploymentLogTailDto(
    DeploymentSummaryDto Deployment,
    int TotalLogLines,
    IReadOnlyList<DeploymentLogLineDto> Tail);

public sealed record DeploymentLogLineDto(
    int Sequence,
    DateTimeOffset Timestamp,
    string Level,
    string Message);
