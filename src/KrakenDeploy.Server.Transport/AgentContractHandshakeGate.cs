using System.Globalization;
using KrakenDeploy.Contracts;
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

    // NO refusal throttle here, and its removal is the fix for two defects rather than a
    // simplification. A per-(target, value) 10-minute window sat in front of
    // recorder.RecordAsync, so it also suppressed the target's Online→Offline transition and
    // its UI push — reconciled STATE, not an event stream — which meant a recorder that threw
    // once (the best-effort case this path explicitly tolerates) left the fleet reading green
    // for the whole window, the exact failure the recorder exists to prevent. Its documented
    // MaxThrottleEntries cap also bounded nothing: the insert ran unconditionally after a
    // prune that only evicted expired keys, so a target varying the header grew the table
    // without limit AND bypassed the throttle, while every refusal past the cap paid a full
    // O(n) scan on the negotiate path.
    //
    // What bounds the rate instead, without any state here: the agent takes
    // AgentReconnectPolicy's 5-minute operator-action lane on a 426, and the recorder's write
    // is conditional on Status == Online so a repeat refusal is a no-op read rather than a
    // write. If a hostile target ever makes this a problem, the answer is a limiter on the
    // endpoint (UseRateLimiter is already in the pipeline), not per-middleware memory that
    // silently gates state reconciliation.

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

    public async Task InvokeAsync(HttpContext context, IAgentContractRefusalRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(recorder);

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
        var claimed = context.User.FindFirst(
            System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        // ── Answer FIRST, record after ──────────────────────────────────────────────────
        // The refusal is written before anything that touches a database, and the order is the
        // fix rather than a style choice: the recording half needs a resolved tenant DB, and
        // with Npgsql's EnableRetryOnFailure a slow one can take seconds. Doing it first put
        // that latency on the negotiate's critical path, so a struggling database turned a
        // clean, diagnosable 426 into a client-side TIMEOUT — the agent then paces on the wrong
        // lane and the operator is told nothing useful.
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        context.Response.Headers[AgentContract.ServerVersionHeader] =
            AgentContract.CurrentVersion.ToString(CultureInfo.InvariantCulture);
        context.Response.ContentType = "text/plain; charset=utf-8";

        try
        {
            // CancellationToken.None, not context.RequestAborted: an agent that drops the
            // transport mid-refusal must not turn this into an OperationCanceledException
            // escaping into UseSerilogRequestLogging (one Error per refusal) and
            // UseExceptionHandler, which then tries to render onto an aborted response. The
            // catch covers the residual the token cannot — an already-faulted response pipe
            // throws regardless — and that must stay a completed refusal, not an unhandled
            // fault.
            await context.Response.WriteAsync(
                $"This server requires agent wire contract v{AgentContract.CurrentVersion}; " +
                $"this agent presented {presented}. {remedy}",
                CancellationToken.None).ConfigureAwait(false);

            // COMPLETE the response, and this is the line that makes "answer first" true.
            // WriteAsync alone only writes into the body; the response is not finished until
            // the pipeline returns, so with no ContentLength (hence chunked) a client reading
            // to completion — which HttpConnection.NegotiateAsync does — stayed blocked for the
            // whole duration of the recording below. Measured on Kestrel: 3056 ms without this
            // call against 1 ms with it, for a 3-second recorder. That latency is exactly what
            // turned a diagnosable 426 into a client-side TIMEOUT on a slow tenant database —
            // and a timeout is not an HttpRequestException with StatusCode 426, so the agent
            // could not classify it and never opened its self-upgrade escape hatch.
            await context.Response.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Writing the 426 refusal body failed (the agent dropped the transport); " +
                "the refusal itself stands.");
        }

        logger.LogWarning(
            "Agent handshake REFUSED for target {TargetId}: contract {Presented} != server " +
            "v{Required}. {Remedy}",
            claimed ?? "(unauthenticated)", presented, AgentContract.CurrentVersion, remedy);

        // Recorded against the target when the handshake carried a usable identity, so an
        // operator learns WHICH agent is skewed rather than which address connected.
        //
        // BEST-EFFORT, and that is load-bearing rather than lazy: this needs a resolved tenant
        // database. If it fails the refusal must still be a clean, actionable 426 — which it
        // now is unconditionally, because the response is already written above.
        if (Guid.TryParse(claimed, out var targetId))
        {
            try
            {
                await recorder.RecordAsync(targetId, presented, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not record the contract refusal for target {TargetId}; the " +
                    "connection is still refused and the warning above still stands.", targetId);
            }
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
        => string.IsNullOrEmpty(value)
            ? "\"\""
            // Shared, rune-safe: a header value can carry an astral character, and slicing by
            // code unit could leave a lone surrogate that Npgsql then refuses to persist —
            // losing the very audit row this echo exists to populate.
            : KrakenDeploy.Execution.TextBudget.Describe(value, MaxEchoedValueLength);

}
