using System.ComponentModel;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KrakenDeploy.Mcp.Tools;

/// <summary>
/// M11.B — deployment-centric MCP tools. Each wraps a shared context
/// builder (or service) with arg validation + an Mcp.ToolInvoked audit
/// row, and returns a slim DTO the SDK serialises to JSON.
/// </summary>
[McpServerToolType]
public sealed class DeploymentTools
{
    [McpServerTool(Name = "list_failed_deployments")]
    [Description(
        "List recent failed (or warning) deployments, newest first. " +
        "Optionally filter by environment name, project slug, and a " +
        "'within the last N hours' window. The starting point for " +
        "'what's broken right now?'.")]
    public static async Task<IReadOnlyList<DeploymentSummaryDto>> ListFailedDeploymentsAsync(
        DeploymentContextBuilder builder,
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        IAuditLog audit,
        [Description("Filter to this environment name (optional).")] string? environmentName = null,
        [Description("Filter to this project slug (optional).")] string? projectSlug = null,
        [Description("Only deployments created within the last N hours (optional).")] int? sinceHours = null,
        CancellationToken ct = default)
    {
        // T1-9: read tools authorize too — mirror the REST DeploymentView gate.
        await McpToolAuth.EnsureAsync(
            permissions, httpContext, "list_failed_deployments", Permission.DeploymentView, audit, ct)
            .ConfigureAwait(false);
        await McpAudit.ToolInvokedAsync(audit, "list_failed_deployments",
            $"env={environmentName}, project={projectSlug}, sinceHours={sinceHours}", "ok", ct)
            .ConfigureAwait(false);
        return await builder.ListAsync(
            onlyFailed: true, environmentName: environmentName,
            projectSlug: projectSlug, sinceHours: sinceHours, ct: ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "get_deployment_log")]
    [Description(
        "Get a deployment's summary plus the tail of its log (default last " +
        "50 lines). For the complete log, read the " +
        "kraken://deployments/{id}/log resource instead.")]
    public static async Task<DeploymentLogTailDto> GetDeploymentLogAsync(
        DeploymentContextBuilder builder,
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        IAuditLog audit,
        [Description("The deployment id (GUID).")] Guid deploymentId,
        [Description("How many trailing log lines to return (default 50, max 1000).")] int tailLines = 50,
        CancellationToken ct = default)
    {
        await McpToolAuth.EnsureAsync(
            permissions, httpContext, "get_deployment_log", Permission.DeploymentView, audit, ct)
            .ConfigureAwait(false);
        var effectiveTail = tailLines <= 0 ? 50 : tailLines;
        var result = await builder.GetLogTailAsync(deploymentId, effectiveTail, ct).ConfigureAwait(false);
        if (result is null)
        {
            await McpAudit.ToolInvokedAsync(audit, "get_deployment_log",
                $"deploymentId={deploymentId}", "not-found", ct).ConfigureAwait(false);
            throw new McpException($"No deployment found with id '{deploymentId}'.");
        }
        await McpAudit.ToolInvokedAsync(audit, "get_deployment_log",
            $"deploymentId={deploymentId}, tailLines={effectiveTail}", "ok", ct).ConfigureAwait(false);
        return result;
    }

    [McpServerTool(Name = "get_deployment_diff")]
    [Description(
        "Show what changed in a deployment vs the last successful run of the " +
        "same project + environment: release version, package versions, " +
        "variable names (not values), and target set. The fastest way to " +
        "spot a regression's cause.")]
    public static async Task<DeploymentDiffDto> GetDeploymentDiffAsync(
        DeploymentDiffBuilder builder,
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        IAuditLog audit,
        [Description("The deployment id (GUID).")] Guid deploymentId,
        CancellationToken ct)
    {
        await McpToolAuth.EnsureAsync(
            permissions, httpContext, "get_deployment_diff", Permission.DeploymentView, audit, ct)
            .ConfigureAwait(false);
        var diff = await builder.BuildAsync(deploymentId, ct).ConfigureAwait(false);
        if (diff is null)
        {
            await McpAudit.ToolInvokedAsync(audit, "get_deployment_diff",
                $"deploymentId={deploymentId}", "not-found", ct).ConfigureAwait(false);
            throw new McpException($"No deployment found with id '{deploymentId}'.");
        }
        await McpAudit.ToolInvokedAsync(audit, "get_deployment_diff",
            $"deploymentId={deploymentId}", "ok", ct).ConfigureAwait(false);
        return diff;
    }

    [McpServerTool(Name = "get_step_config")]
    [Description(
        "Get the complete, unredacted config dictionary for one step of a " +
        "deployment's frozen process snapshot, addressed by zero-based index.")]
    public static async Task<IReadOnlyDictionary<string, string>> GetStepConfigAsync(
        IDbContextFactory<KrakenDbContext> dbFactory,
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        IAuditLog audit,
        [Description("The deployment id (GUID).")] Guid deploymentId,
        [Description("Zero-based step index into the process snapshot.")] int stepIndex,
        CancellationToken ct)
    {
        // Step config is process detail — gate on ProcessView (matches REST).
        await McpToolAuth.EnsureAsync(
            permissions, httpContext, "get_step_config", Permission.ProcessView, audit, ct)
            .ConfigureAwait(false);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var release = await db.Deployments.AsNoTracking()
            .Where(d => d.Id == deploymentId)
            .Select(d => d.Release)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (release is null)
        {
            await McpAudit.ToolInvokedAsync(audit, "get_step_config",
                $"deploymentId={deploymentId}", "not-found", ct).ConfigureAwait(false);
            throw new McpException($"No deployment found with id '{deploymentId}'.");
        }

        var steps = release.ProcessSnapshot.OrderBy(s => s.SortOrder).ToList();
        if (stepIndex < 0 || stepIndex >= steps.Count)
        {
            await McpAudit.ToolInvokedAsync(audit, "get_step_config",
                $"deploymentId={deploymentId}, stepIndex={stepIndex}", "out-of-range", ct).ConfigureAwait(false);
            throw new McpException(
                $"Step index {stepIndex} is out of range (snapshot has {steps.Count} step(s)).");
        }
        await McpAudit.ToolInvokedAsync(audit, "get_step_config",
            $"deploymentId={deploymentId}, stepIndex={stepIndex}", "ok", ct).ConfigureAwait(false);
        return steps[stepIndex].Config;
    }

    [McpServerTool(Name = "retry_deployment")]
    [Description(
        "Re-run a deployment: creates a NEW deployment of the same release " +
        "to the same environment + target set. Returns the new deployment " +
        "id. Requires the DeploymentCreate permission — evaluated against " +
        "the API key's owning user (and its bound Space when the key is " +
        "Space-restricted).")]
    public static async Task<RetryDeploymentResultDto> RetryDeploymentAsync(
        IDbContextFactory<KrakenDbContext> dbFactory,
        DeploymentService deploymentService,
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        IAuditLog audit,
        [Description("The deployment id (GUID) to re-run.")] Guid deploymentId,
        CancellationToken ct)
    {
        // The one mutating deployment tool — closes the M11.B deferral: the
        // description used to CLAIM enforcement that didn't exist. EnsureAsync also
        // returns the acting user (API-key owner) for provenance stamping.
        var (actingUserId, actingDisplay) = await McpToolAuth.EnsureAsync(
            permissions, httpContext, "retry_deployment", Permission.DeploymentCreate, audit, ct)
            .ConfigureAwait(false);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var source = await db.Deployments.AsNoTracking()
            .Include(d => d.Targets)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct).ConfigureAwait(false);
        if (source is null)
        {
            await McpAudit.ToolInvokedAsync(audit, "retry_deployment",
                $"deploymentId={deploymentId}", "not-found", ct).ConfigureAwait(false);
            throw new McpException($"No deployment found with id '{deploymentId}'.");
        }

        // Reproduce the source's target set from the assignments join,
        // first-assigned first so the retry keeps the same primary target.
        var targetIds = source.Targets
            .OrderBy(a => a.AddedUtc).ThenBy(a => a.TargetId)
            .Select(a => a.TargetId)
            .ToList();
        if (targetIds.Count == 0)
        {
            await McpAudit.ToolInvokedAsync(audit, "retry_deployment",
                $"deploymentId={deploymentId}", "no-target", ct).ConfigureAwait(false);
            throw new McpException("Source deployment has no target — cannot retry.");
        }

        // EnsureAsync yields Guid.Empty when the principal lacks a parseable id;
        // map that to null so the created_by_user_id FK stays clean.
        Guid? initiatorUserId = actingUserId == Guid.Empty ? null : actingUserId;
        Deployment child;
        try
        {
            // T1-8: CreateAsync runs the authoritative strict sub-Space check
            // against the API-key owner. The Space-level EnsureAsync above is the
            // coarse gate; this rejects e.g. an Environment=Test-scoped key
            // retrying a Prod deployment.
            child = await deploymentService.CreateAsync(
                releaseId:           source.ReleaseId,
                environmentId:       source.EnvironmentId,
                targetId:            targetIds[0],
                initiator:           TaskInitiator.Mcp(
                                         initiatorUserId, actingDisplay,
                                         detail: $"retry_deployment;source:{deploymentId}"),
                caller:              CallerAuthorization.ForUser(httpContext.HttpContext!.User),
                tenantId:            source.TenantId,
                scheduledFor:        null,
                additionalTargetIds: targetIds.Skip(1).ToList(),
                failureMode:         source.FailureMode,
                promptedValues:      deploymentService.ReadPromptedValuesForRetry(source.FormValues),
                ct:                  ct).ConfigureAwait(false);
        }
        catch (AuthorizationException ex)
        {
            await McpAudit.ToolInvokedAsync(audit, "retry_deployment",
                $"deploymentId={deploymentId}", "forbidden", ct).ConfigureAwait(false);
            throw new McpException(ex.Message);
        }

        await McpAudit.ToolInvokedAsync(audit, "retry_deployment",
            $"sourceId={deploymentId}, newId={child.Id}", "ok", ct).ConfigureAwait(false);
        return new RetryDeploymentResultDto(child.Id, source.Id);
    }
}

/// <summary>Result of <c>retry_deployment</c> — the new deployment's id +
/// the source it was cloned from.</summary>
public sealed record RetryDeploymentResultDto(Guid NewDeploymentId, Guid SourceDeploymentId);
