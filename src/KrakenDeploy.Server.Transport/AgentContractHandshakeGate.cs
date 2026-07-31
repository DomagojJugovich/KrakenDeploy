using System.Collections.Concurrent;
using System.Globalization;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Refuses an agent whose wire-contract version does not match this server's, on the
/// SignalR HANDSHAKE — before the connection is admitted. Past this gate, connected means
/// verified means dispatchable, so nothing downstream needs a second notion of
/// "eligible". Why the check lives here rather than in a hub method, and what the agent
/// does with the refusal, is documented once in <c>docs/agent-wire-contract.md</c>.
/// <para>
/// Scoped by ENDPOINT METADATA (<see cref="RequiresAgentContract"/>), not by a path
/// string: a path match fails OPEN on any route drift, and it also fires on
/// <c>/hubs/agent/&lt;anything&gt;</c>, which matches no endpoint and therefore carries no
/// authorize metadata for <c>UseAuthorization</c> to enforce.
/// </para>
/// <para>
/// 426 Upgrade Required is the accurate status: the request was well-formed and
/// authenticated, and the client must change protocol version to proceed. It is
/// deliberately NOT 401/403 — those mean "re-enroll this agent", a different operator
/// action, and the agent's reconnect policy routes them differently.
/// </para>
/// <para>
/// VERIFIED, because the round-4 design rested on the opposite assumption: the SignalR
/// client's <c>HttpConnection.NegotiateAsync</c> calls <c>EnsureSuccessStatusCode()</c>
/// before reading the response, so neither the body below nor
/// <see cref="AgentContract.ServerVersionHeader"/> reaches the agent — its exception
/// message is only "Response status code does not indicate success: 426 (Upgrade
/// Required)." Both are written anyway because they are what an operator sees when they
/// reproduce the refusal by hand (curl / browser devtools), but the agent-side
/// diagnosis has to come from the status code alone, and does
/// (<c>AgentReconnectPolicy</c>). The number an operator needs is therefore on the SERVER
/// log line below, which is the only place both versions appear together.
/// </para>
/// </summary>
public sealed class AgentContractHandshakeGate(
    RequestDelegate next,
    TimeProvider timeProvider,
    ILogger<AgentContractHandshakeGate> logger)
{
    /// <summary>
    /// How much of the client-supplied header value is echoed into the log line and the
    /// audit row. The header is attacker-influenced (a compromised target with a valid
    /// agent JWT, up to Kestrel's 32 KB header limit) and
    /// <c>AuditEntry.Details</c> flows on to the webhook, e-mail and AI-inspect
    /// transports, the last of which interpolates it into an LLM prompt. A contract
    /// version is one or two digits, so anything past this is not diagnostic.
    /// </summary>
    internal const int MaxEchoedValueLength = 24;

    /// <summary>
    /// A refusal is a per-target STATE, not an event stream: an agent that is skewed is
    /// skewed until an operator acts. Report each distinct (target, presented value) at
    /// most once per window so a fleet-wide skew after a server upgrade cannot turn into
    /// a sustained audit-INSERT and log flood — which, because the subscription poller
    /// forwards audit rows off-premises, would also mean a sustained webhook/e-mail fan-out.
    /// The 426 itself is NEVER throttled; only its reporting is.
    /// </summary>
    internal static readonly TimeSpan RefusalReportInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Bound on the throttle table. Keys are (target id, presented value) and the gate is
    /// unreachable without a valid agent JWT, so real-world cardinality is the fleet size;
    /// the cap only stops a compromised target from growing it without limit by varying
    /// the header. On overflow the oldest-expired entries are dropped, and if none have
    /// expired the report goes out unthrottled — losing the throttle is the safe direction.
    /// </summary>
    internal const int MaxThrottleEntries = 8192;

    // Middleware instances are created once per pipeline build, so this state is shared
    // across requests by design. Monotonic timestamps (never GetUtcNow) so a domain-joined
    // host's clock step cannot disarm or freeze the throttle.
    private readonly ConcurrentDictionary<(string Target, string Presented), long> _reported = new();

    /// <summary>
    /// True when the matched endpoint declares <see cref="RequiresAgentContract"/>. Shared
    /// with <see cref="AgentContractHandshakeGateExtensions.UseAgentContractGate"/> so the
    /// branch condition and the gate's own guard cannot drift apart.
    /// </summary>
    internal static bool GuardsEndpoint(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetEndpoint()?.Metadata.GetMetadata<RequiresAgentContract>() is not null;
    }

    public async Task InvokeAsync(HttpContext context, IAuditLog auditLog)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(auditLog);

        // Re-checked here and not only in the mount condition: the gate must be safe to
        // mount any way at all, and an endpoint that does not ask to be guarded must not be.
        if (!GuardsEndpoint(context))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var header = context.Request.Headers[AgentContract.VersionHeader];

        // Exactly ONE value, digits only. Both restrictions are load-bearing:
        //
        //  * StringValues.ToString() joins multiple values with ", ", so two
        //    "X-KD-Contract: 4" headers — which YARP's RequestHeader transform produces
        //    unless Set is used, and Caddy's header_up likewise — would present as "4, 4".
        //    Reading Count directly keeps that case diagnosable instead of reporting it as
        //    a garbled version and sending the operator to upgrade a correct agent.
        //  * NumberStyles.None (digits only, after an explicit trim) rather than
        //    NumberStyles.Integer: Integer permits a leading sign and thousands-adjacent
        //    parses that a version number has no use for, and the stricter form is what
        //    keeps a joined "4,4" from ever being read as 44.
        var version = 0;
        var parsed = header.Count == 1
            && int.TryParse(
                header[0].AsSpan().Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                out version);

        if (parsed && version == AgentContract.CurrentVersion)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Fail closed on absent, duplicated, unparseable AND mismatched alike. An agent old
        // enough not to send the header at all is exactly the case that must be refused, so
        // "missing" cannot mean "compatible", and a garbled value is not evidence of
        // anything better.
        var (presented, remedy) = Describe(header, parsed, version);

        // The gate is mounted after UseAuthorization and only on endpoints carrying the
        // hub's [Authorize(AuthenticationSchemes = "AgentJwt")], so in practice this claim
        // is always present and always the agent's target id. Tolerating null keeps the
        // middleware safe to mount elsewhere rather than asserting a pipeline shape it
        // cannot see.
        var targetId = context.User.FindFirst(
            System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (ShouldReport(targetId, presented))
        {
            logger.LogWarning(
                "Agent handshake REFUSED for target {TargetId}: contract {Presented} != " +
                "server v{Required}. {Remedy} Further refusals of the same value from this " +
                "target are suppressed for {Interval}.",
                targetId ?? "(unauthenticated)", presented, AgentContract.CurrentVersion,
                remedy, RefusalReportInterval);

            // Audited against the target when the handshake carried a usable identity, so
            // an operator learns WHICH agent is skewed rather than which address connected.
            //
            // BEST-EFFORT, and that is load-bearing rather than lazy: the audit write needs
            // a resolved tenant database. If recording fails the refusal must still be a
            // clean, actionable 426 — letting the exception escape would turn "upgrade your
            // agent" into an opaque 500 for the agent and a server fault for the operator,
            // which is strictly worse than a missing row.
            if (targetId is not null)
            {
                try
                {
                    await auditLog.RecordAsync(
                        AuditEventType.AgentContractVersionRejected,
                        subjectType: "DeploymentTarget",
                        subjectId:   targetId,
                        details:     $"SentContract={presented}, " +
                                     $"RequiredContract={AgentContract.CurrentVersion}",
                        // Explicit, and required for correctness rather than tidiness:
                        // AuditLogService falls back to the ambient HTTP principal's
                        // NameIdentifier when no attribution is supplied, and on this path
                        // that principal is the AGENT — so the row would claim a user whose
                        // id is really a DeploymentTarget's, and render as "Unknown".
                        userId:      null,
                        userDisplay: "System",
                        // NOT context.RequestAborted: an agent that drops the transport
                        // mid-refusal must not turn this into an OperationCanceledException
                        // escaping into UseSerilogRequestLogging and UseExceptionHandler.
                        ct:          CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not audit the contract refusal for target {TargetId}; the " +
                        "connection is still refused.", targetId);
                }
            }
        }

        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        context.Response.Headers[AgentContract.ServerVersionHeader] =
            AgentContract.CurrentVersion.ToString(CultureInfo.InvariantCulture);
        context.Response.ContentType = "text/plain; charset=utf-8";

        try
        {
            // CancellationToken.None for the same reason as the audit write above. The
            // catch covers the residual case the token cannot: the response pipe of an
            // already-aborted connection faults on write regardless of the token, and that
            // must stay a completed refusal rather than becoming an unhandled fault.
            await context.Response.WriteAsync(
                $"This server requires agent wire contract v{AgentContract.CurrentVersion}; " +
                $"this agent presented {presented}. {remedy}",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Writing the 426 refusal body failed (the agent dropped the transport); " +
                "the refusal itself stands.");
        }
    }

    /// <summary>
    /// Turns the raw header into an operator-facing description of what was presented and
    /// what to do about it. The remedy differs by cause: "upgrade the agent" is actively
    /// misleading when the real fault is an intermediary rewriting the header, because the
    /// agent binary may already be correct.
    /// </summary>
    internal static (string Presented, string Remedy) Describe(
        Microsoft.Extensions.Primitives.StringValues header, bool parsed, int version)
    {
        if (header.Count == 0)
        {
            return ("absent",
                "Update the agent binary — this agent predates the handshake header.");
        }

        if (header.Count > 1)
        {
            return ($"duplicated ({header.Count} values: {Truncate(header.ToString())})",
                "An intermediary appended a second value rather than replacing it " +
                "(YARP RequestHeader without Set, or Caddy header_up); the agent binary " +
                "may be correct. Fix the proxy transform.");
        }

        return parsed
            ? ($"v{version.ToString(CultureInfo.InvariantCulture)}",
                "Update the agent binary.")
            : ($"unparseable ({Truncate(header[0])})",
                "The value is not a version number — check for an intermediary rewriting " +
                "the header before upgrading the agent.");
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }
        return value.Length <= MaxEchoedValueLength
            ? $"\"{value}\""
            : $"\"{value[..MaxEchoedValueLength]}\"… ({value.Length} chars)";
    }

    /// <summary>
    /// Whether this (target, presented value) pair is due a log line and an audit row.
    /// Reports the first occurrence immediately, then at most once per
    /// <see cref="RefusalReportInterval"/> — so a changed skew value reports at once
    /// instead of hiding behind the previous one's window.
    /// </summary>
    private bool ShouldReport(string? targetId, string presented)
    {
        var key = (targetId ?? "(unauthenticated)", presented);
        var now = timeProvider.GetTimestamp();

        if (_reported.TryGetValue(key, out var last)
            && timeProvider.GetElapsedTime(last) < RefusalReportInterval)
        {
            return false;
        }

        if (_reported.Count >= MaxThrottleEntries)
        {
            PruneExpired();
        }

        _reported[key] = now;
        return true;
    }

    private void PruneExpired()
    {
        foreach (var (key, stamp) in _reported)
        {
            if (timeProvider.GetElapsedTime(stamp) >= RefusalReportInterval)
            {
                _reported.TryRemove(key, out _);
            }
        }
    }
}
