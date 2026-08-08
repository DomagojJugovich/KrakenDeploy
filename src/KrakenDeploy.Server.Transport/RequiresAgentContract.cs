using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Endpoint metadata: only an agent presenting
/// <see cref="KrakenDeploy.Contracts.AgentContract.CurrentVersion"/> on the handshake may
/// reach the endpoint this is attached to. <see cref="AgentContractHandshakeGate"/> keys off
/// this rather than off a path string.
/// <para>
/// The distinction is fail-open versus fail-closed. A path match answers "does this URL look
/// like the agent hub", which stops being true the moment the route is renamed, versioned,
/// or rewritten by an edge proxy — and the failure mode of a stale path match is that the
/// gate silently admits every agent while routing still reaches the hub. The metadata rides
/// the ENDPOINT, so it moves with the route by construction: if the hub is still mapped, the
/// marker is still on it, and if it is not mapped there is no endpoint and nothing to guard.
/// </para>
/// </summary>
public sealed class RequiresAgentContract;

/// <summary>
/// Mounts <see cref="AgentContractHandshakeGate"/> at the host composition root, matching
/// the repo's <c>UseMaintenanceMode()</c> / <c>UseKrakenMcpEnabledGate()</c> convention.
/// </summary>
public static class AgentContractHandshakeGateExtensions
{
    /// <summary>
    /// Branches into the gate ONLY for endpoints carrying
    /// <see cref="RequiresAgentContract"/>.
    /// <para>
    /// <c>UseWhen</c> rather than a bare <c>UseMiddleware</c> because the gate
    /// method-injects the scoped <c>IAuditLog</c>, and <c>UseMiddleware</c> resolves
    /// method-injected services from <c>RequestServices</c> BEFORE the body runs — so a
    /// globally mounted gate makes every request in the application (static assets and
    /// health probes included) build an <c>AuditLogService</c> and its
    /// <c>DbContextOptions</c> just to discover it has nothing to do.
    /// </para>
    /// <para>
    /// Must be mounted AFTER <c>UseAuthentication</c>/<c>UseAuthorization</c>: the refusal
    /// is audited against the target named by the agent JWT, and running after
    /// authorization is also what keeps the gate unreachable without a valid agent
    /// credential — the hub endpoint's <c>[Authorize(AuthenticationSchemes = "AgentJwt")]</c>
    /// is enforced first, so a browser session cannot reach the gate at all.
    /// </para>
    /// </summary>
    public static IApplicationBuilder UseAgentContractGate(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseWhen(
            AgentContractHandshakeGate.GuardsEndpoint,
            branch => branch.UseMiddleware<AgentContractHandshakeGate>());
    }
}
