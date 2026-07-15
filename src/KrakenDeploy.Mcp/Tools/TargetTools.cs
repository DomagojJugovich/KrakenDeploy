using System.ComponentModel;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Services.Ai.ContextBuilders;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KrakenDeploy.Mcp.Tools;

/// <summary>M11.B — target-centric MCP tools.</summary>
[McpServerToolType]
public sealed class TargetTools
{
    [McpServerTool(Name = "get_target_health")]
    [Description(
        "Get a deployment target's health: status, last-seen heartbeat, OS / " +
        "agent version, roles, and its most recent deployment result. Use " +
        "when diagnosing whether a failure is target-side (offline, stale agent).")]
    public static async Task<TargetHealthDto> GetTargetHealthAsync(
        TargetHealthBuilder builder,
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        IAuditLog audit,
        [Description("The target's name.")] string targetName,
        CancellationToken ct)
    {
        await McpToolAuth.EnsureAsync(
            permissions, httpContext, "get_target_health", Permission.MachineView, audit, ct)
            .ConfigureAwait(false);
        var health = await builder.GetByNameAsync(targetName, ct).ConfigureAwait(false);
        if (health is null)
        {
            await McpAudit.ToolInvokedAsync(audit, "get_target_health",
                $"targetName={targetName}", "not-found", ct).ConfigureAwait(false);
            throw new McpException($"No target found with name '{targetName}'.");
        }
        await McpAudit.ToolInvokedAsync(audit, "get_target_health",
            $"targetName={targetName}", "ok", ct).ConfigureAwait(false);
        return health;
    }

    [McpServerTool(Name = "query_targets")]
    [Description(
        "List deployment targets, optionally filtered by role and/or " +
        "environment (targets that have deployed to that environment). " +
        "Returns slim rows: name, status, roles, last-seen.")]
    public static async Task<IReadOnlyList<TargetSummaryDto>> QueryTargetsAsync(
        TargetHealthBuilder builder,
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        IAuditLog audit,
        [Description("Filter to targets carrying this role (optional).")] string? role,
        [Description("Filter to targets used in this environment (optional).")] string? environmentName,
        CancellationToken ct)
    {
        await McpToolAuth.EnsureAsync(
            permissions, httpContext, "query_targets", Permission.MachineView, audit, ct)
            .ConfigureAwait(false);
        await McpAudit.ToolInvokedAsync(audit, "query_targets",
            $"role={role}, env={environmentName}", "ok", ct).ConfigureAwait(false);
        return await builder.QueryAsync(role, environmentName, ct).ConfigureAwait(false);
    }
}

/// <summary>M11.B — release-history MCP tool.</summary>
[McpServerToolType]
public sealed class ReleaseTools
{
    [McpServerTool(Name = "get_release_history")]
    [Description(
        "List a project's releases, newest first, as manifests (version, " +
        "channel, step count, created date). Useful for 'what was the last " +
        "known-good version?' style questions.")]
    public static async Task<IReadOnlyList<ReleaseManifestDto>> GetReleaseHistoryAsync(
        ReleaseContextBuilder builder,
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        IAuditLog audit,
        [Description("The project slug.")] string projectSlug,
        [Description("How many releases to return (default 20, max 100).")] int count,
        CancellationToken ct)
    {
        await McpToolAuth.EnsureAsync(
            permissions, httpContext, "get_release_history", Permission.ReleaseView, audit, ct)
            .ConfigureAwait(false);
        var effectiveCount = count <= 0 ? 20 : count;
        await McpAudit.ToolInvokedAsync(audit, "get_release_history",
            $"projectSlug={projectSlug}, count={effectiveCount}", "ok", ct).ConfigureAwait(false);
        return await builder.GetHistoryAsync(projectSlug, effectiveCount, ct).ConfigureAwait(false);
    }
}
