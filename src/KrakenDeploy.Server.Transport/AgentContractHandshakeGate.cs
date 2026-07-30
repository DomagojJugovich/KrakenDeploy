using System.Globalization;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Refuses an agent whose wire-contract version does not match this server's, on the
/// SignalR HANDSHAKE — before the connection is admitted.
/// <para>
/// The check used to live inside <c>AgentHub.RegisterAsync</c>, which is a hub method and
/// therefore runs only once the connection is established and tracked. That ordering was
/// the source of a whole family of defects rather than one: the server had to admit a
/// connection it could not yet trust, dispatch had to be gated on a separate
/// "has registered" flag, the offline mark and the mid-wave disconnect monitor had to be
/// taught the difference between liveness and eligibility, and a failed registration had
/// to be answered by aborting the connection to force a retry the agent would not ask for
/// — an abort that, because <c>Context.Abort()</c> drops the transport rather than closing
/// it, the client's automatic reconnect retried immediately, forever.
/// </para>
/// <para>
/// Refusing here removes the state instead of guarding it: past this gate, connected means
/// verified means dispatchable. It also puts the refusal on a path the agent already paces
/// — a 4xx from the handshake fails <c>HubConnection.StartAsync</c>, which the agent's
/// supervision loop retries on a backoff, whereas an abort after connect does not.
/// </para>
/// <para>
/// 426 Upgrade Required is the accurate status: the request was well-formed and
/// authenticated, and the client must change protocol version to proceed. It is
/// deliberately NOT 401/403 — those mean "re-enroll this agent", a different operator
/// action, and the agent's reconnect policy routes them to a different lane.
/// </para>
/// </summary>
public sealed class AgentContractHandshakeGate(
    RequestDelegate next,
    ILogger<AgentContractHandshakeGate> logger)
{
    /// <summary>The hub path this gate protects.</summary>
    internal const string HubPath = "/hubs/agent";

    public async Task InvokeAsync(HttpContext context, IAuditLog auditLog)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(auditLog);

        if (!context.Request.Path.StartsWithSegments(
                HubPath, StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var sent = context.Request.Headers[AgentContract.VersionHeader].ToString();
        if (int.TryParse(sent, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            && version == AgentContract.CurrentVersion)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Fail closed on absent, unparseable AND mismatched alike. An agent old enough not
        // to send the header at all is exactly the case that must be refused, so "missing"
        // cannot be treated as "compatible" — and a garbled value is not evidence of
        // anything better.
        var targetId = context.User.FindFirst(
            System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var describeSent = string.IsNullOrEmpty(sent) ? "absent" : $"v{sent}";

        logger.LogWarning(
            "Agent handshake REFUSED for target {TargetId}: contract {Sent} != server " +
            "v{Required}. Update the agent binary; it will retry on the slow lane.",
            targetId ?? "(unauthenticated)", describeSent, AgentContract.CurrentVersion);

        // Audited against the target when the handshake carried a usable identity. This
        // runs after authentication precisely so the row can name the target rather than
        // an address — an operator needs to know WHICH agent is skewed.
        //
        // BEST-EFFORT, and that is load-bearing rather than lazy: the audit write needs a
        // resolved tenant database, and this gate deliberately sits early in the pipeline.
        // If recording fails the refusal must still be a clean, actionable 426 — letting the
        // exception escape would turn "upgrade your agent" into an opaque 500 for the agent
        // and a noisy server fault for the operator, which is strictly worse than a missing
        // row. The log line above always lands.
        if (targetId is not null)
        {
            try
            {
                await auditLog.RecordAsync(
                    AuditEventType.AgentContractVersionRejected,
                    subjectType: "DeploymentTarget",
                    subjectId:   targetId,
                    details:     $"SentContract={describeSent}, " +
                                 $"RequiredContract={AgentContract.CurrentVersion}",
                    ct:          context.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Could not audit the contract refusal for target {TargetId}; the " +
                    "connection is still refused.", targetId);
            }
        }

        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        context.Response.Headers[AgentContract.ServerVersionHeader] =
            AgentContract.CurrentVersion.ToString(CultureInfo.InvariantCulture);
        await context.Response.WriteAsync(
            $"This server requires agent wire contract v{AgentContract.CurrentVersion}; " +
            $"this agent presented {describeSent}. Update the agent binary.",
            context.RequestAborted).ConfigureAwait(false);
    }
}
