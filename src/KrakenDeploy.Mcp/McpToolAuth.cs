using System.Security.Claims;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Security;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;

namespace KrakenDeploy.Mcp;

/// <summary>
/// Shared permission gate for MCP tools that mutate state. Resolves the
/// caller's API-key principal from the request, evaluates the required
/// <see cref="Permission"/> via <c>IPermissionEvaluator</c> and throws a
/// 403-shaped <see cref="McpException"/> on failure — hoisted from
/// <c>AdhocTools</c> so every gated tool shares one implementation.
/// </summary>
internal static class McpToolAuth
{
    /// <summary>
    /// Ensures the caller holds <paramref name="required"/>. Returns the
    /// resolved user id + display name for audit/ownership stamping.
    /// </summary>
    internal static async Task<(Guid UserId, string Display)> EnsureAsync(
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        string toolName,
        Permission required,
        IAuditLog audit,
        CancellationToken ct)
    {
        var user = httpContext.HttpContext?.User;
        if (user is null || user.Identity?.IsAuthenticated != true)
        {
            await McpAudit.ToolInvokedAsync(audit, toolName, "(no principal)", "unauthorised", ct)
                .ConfigureAwait(false);
            throw new McpException(
                "MCP request has no authenticated principal — verify the X-Api-Key " +
                $"header carries a key bound to an operator with {required}.");
        }

        bool allowed;
        var restriction = user.FindFirst(KrakenClaimTypes.ApiKeySpace)?.Value;
        if (restriction is not null && Guid.TryParse(restriction, out var space))
        {
            // Restricted key: caged to its one Space (the evaluator enforces the
            // cage too — a check outside that Space is denied regardless).
            allowed = await permissions
                .HasPermissionAsync(user, required, new PermissionScope(SpaceId: space), ct: ct)
                .ConfigureAwait(false);
        }
        else
        {
            // Unrestricted key = "act wherever the owner has access" (per the
            // ApiKey.SpaceId contract). A single system-wide (null-SpaceId)
            // check only matches SYSTEM-WIDE role assignments, so it would deny
            // the common per-Space DeploymentCreate/AdhocActionsExecute grant.
            // Try system-wide first (covers admins + system-wide grants), then
            // fan out over the owner's accessible Spaces (bounded — typically
            // 1–2 for a service account; each call is (userId,spaceId)-cached).
            allowed = await permissions
                .HasPermissionAsync(user, required, new PermissionScope(), ct: ct)
                .ConfigureAwait(false);
            if (!allowed)
            {
                var spaces = await permissions.GetAccessibleSpaceIdsAsync(user, ct).ConfigureAwait(false);
                foreach (var s in spaces)
                {
                    if (await permissions
                            .HasPermissionAsync(user, required, new PermissionScope(SpaceId: s), ct: ct)
                            .ConfigureAwait(false))
                    {
                        allowed = true;
                        break;
                    }
                }
            }
        }

        if (!allowed)
        {
            var who = user.Identity?.Name ?? "(unknown)";
            await McpAudit.ToolInvokedAsync(audit, toolName, $"user={who}", "permission-denied", ct)
                .ConfigureAwait(false);
            throw new McpException(
                $"Caller does not have Permission.{required}. An operator may need to " +
                "grant the API key's owning account that permission first.");
        }

        var userIdRaw = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var userId = Guid.TryParse(userIdRaw, out var u) ? u : Guid.Empty;
        var display = user.Identity?.Name ?? "mcp-client";
        return (userId, display);
    }
}
