using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Services.Ai.Adhoc;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KrakenDeploy.Mcp.Tools;

/// <summary>
/// M11.E.10 — exposes the ad-hoc-action flow as MCP tools so external AI
/// clients (Claude Desktop, Cursor, …) can DRIVE a session — but cannot
/// auto-approve. The single mutating step (operator approval) stays in the
/// UI: every iteration must be approved + signed in <c>/adhoc/{id}</c> by a
/// human with <see cref="Permission.AdhocActionsExecute"/>. This is the
/// "approval gate still enforced server-side regardless of the source"
/// requirement (M11.E.10) — the MCP entry point only INITIATES + REPORTS
/// state; the safety contract from commits 2-5 (gate, signing, frozen
/// targets, mode immutability, iteration cap) applies identically.
/// </summary>
[McpServerToolType]
public sealed class AdhocTools
{
    [McpServerTool(Name = "run_adhoc_action")]
    [Description(
        "Initiate an ad-hoc agent action: take a natural-language request, " +
        "resolve a frozen target set, ask the server's LLM to propose a " +
        "PowerShell script, run it through the static-analysis gate, and " +
        "create a new session whose iteration 1 awaits operator approval " +
        "at /adhoc/{sessionId}. Returns the proposed script + risk + a " +
        "deep-link for the operator. This tool NEVER auto-approves — every " +
        "execution requires a human with AdhocActionsExecute to approve " +
        "and sign the script in the UI.")]
    public static async Task<AdhocInitiationResult> RunAdhocActionAsync(
        AdhocSessionService sessions,
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        IAuditLog audit,
        [Description("Operator-style natural-language request (e.g. 'check the free disk space on the web tier').")]
        string prompt,
        [Description("Either 'readonly' (Get-/Test-/Measure-* only) or 'mutating' (state changes permitted). Defaults to 'readonly'.")]
        string mode,
        [Description("Explicit target ids (GUIDs) to dispatch against. The set is frozen for the session's life.")]
        IReadOnlyList<Guid> targetIds,
        CancellationToken ct)
    {
        var (userId, display) = await EnsureAuthorisedAsync(
            permissions, httpContext, "run_adhoc_action", audit, ct).ConfigureAwait(false);

        var parsedMode = ParseMode(mode);

        Guid sessionId;
        Guid iterationId;
        try
        {
            sessionId = await sessions.CreateSessionAsync(
                prompt, parsedMode, targetIds, userId, display, ct).ConfigureAwait(false);
            iterationId = await sessions.GenerateFirstIterationAsync(sessionId, ct)
                .ConfigureAwait(false);
        }
        catch (AdhocFeatureUnavailableException ex)
        {
            await McpAudit.ToolInvokedAsync(audit, "run_adhoc_action",
                $"mode={mode}, targets={targetIds.Count}", $"unavailable:{ex.Reason}", ct)
                .ConfigureAwait(false);
            throw new McpException(
                $"Ad-hoc actions are unavailable for this Space: {ex.Reason} — {ex.Message}");
        }
        catch (AdhocGateRejectedException ex)
        {
            // Session was created (frozen target set + iter count of 1 visible
            // in audit) but the generated script tripped the gate — the
            // operator sees no iteration row at /adhoc/{id} and can re-prompt
            // with a different ask. We surface the violation summary to the
            // MCP caller so the LLM can adjust its request.
            await McpAudit.ToolInvokedAsync(audit, "run_adhoc_action",
                $"mode={mode}, targets={targetIds.Count}", "gate-rejected", ct)
                .ConfigureAwait(false);
            throw new McpException(
                "The server's LLM produced a script the static-analysis gate rejected: " +
                $"{ex.Result.Summary}. Try a clearer / narrower prompt, or switch mode if " +
                "the request inherently changes state.");
        }
        catch (ArgumentException ex)
        {
            await McpAudit.ToolInvokedAsync(audit, "run_adhoc_action",
                $"mode={mode}, targets={targetIds.Count}", $"invalid:{ex.Message}", ct)
                .ConfigureAwait(false);
            throw new McpException($"Invalid request: {ex.Message}");
        }

        // Reload the session to surface the proposed-script + risk back to the
        // MCP caller in a single round-trip.
        var session = await sessions.GetSessionAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new McpException("Session vanished immediately after creation.");
        var iter = session.Iterations.SingleOrDefault(i => i.Id == iterationId)
            ?? throw new McpException("Iteration vanished immediately after creation.");

        await McpAudit.ToolInvokedAsync(audit, "run_adhoc_action",
            $"sessionId={sessionId}, mode={parsedMode}, targets={targetIds.Count}", "ok", ct)
            .ConfigureAwait(false);

        return new AdhocInitiationResult(
            SessionId:                 sessionId,
            IterationId:               iter.Id,
            ApprovalUrl:               $"/adhoc/{sessionId:D}",
            Prompt:                    session.Prompt,
            Mode:                      session.Mode.ToString(),
            FrozenTargetIds:           targetIds,
            ProposedScript:            iter.GeneratedScript,
            Description:               iter.Description,
            RiskAssessment:            iter.RiskAssessment,
            ExpectedOutputShape:       iter.ExpectedOutputShape,
            RequiresMutation:          iter.RequiresMutation,
            ApprovalPending:           true,
            HumanApprovalRequiredNote: "An operator with AdhocActionsExecute must approve " +
                                       "this iteration at the ApprovalUrl above before it " +
                                       "runs. This tool cannot self-approve (M11.E.10).");
    }

    [McpServerTool(Name = "get_adhoc_session")]
    [Description(
        "Fetch the current state of an ad-hoc session: status, per-iteration " +
        "approval state, per-target results, and the latest verdict. Useful " +
        "for the MCP client to poll 'did the operator approve my proposed " +
        "action yet?' after calling run_adhoc_action.")]
    public static async Task<AdhocSessionDetailDto> GetAdhocSessionAsync(
        AdhocSessionService sessions,
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        IAuditLog audit,
        [Description("The session id returned by run_adhoc_action.")]
        Guid sessionId,
        CancellationToken ct)
    {
        await EnsureAuthorisedAsync(permissions, httpContext, "get_adhoc_session", audit, ct)
            .ConfigureAwait(false);

        var session = await sessions.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            await McpAudit.ToolInvokedAsync(audit, "get_adhoc_session",
                $"sessionId={sessionId}", "not-found", ct).ConfigureAwait(false);
            throw new McpException(
                $"No ad-hoc session found with id '{sessionId}' (or it's outside your Space).");
        }

        await McpAudit.ToolInvokedAsync(audit, "get_adhoc_session",
            $"sessionId={sessionId}", "ok", ct).ConfigureAwait(false);

        return new AdhocSessionDetailDto(
            SessionId:       session.Id,
            Status:          session.Status.ToString(),
            Mode:            session.Mode.ToString(),
            Prompt:          session.Prompt,
            MaxIterations:   session.MaxIterations,
            CreatedByUser:   session.CreatedByDisplay,
            CreatedUtc:      session.CreatedUtc,
            Iterations:      session.Iterations
                .OrderBy(i => i.IterNumber)
                .Select(MapIteration)
                .ToList());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks the caller's API-key principal has
    /// <see cref="Permission.AdhocActionsExecute"/> in the active Space.
    /// Returns the resolved user id + display for audit + ownership stamping.
    /// Throws <see cref="McpException"/> with a 403-shaped message on failure.
    /// </summary>
    private static async Task<(Guid UserId, string Display)> EnsureAuthorisedAsync(
        IPermissionEvaluator permissions,
        IHttpContextAccessor httpContext,
        string toolName,
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
                "header carries a key bound to an operator with AdhocActionsExecute.");
        }

        var allowed = await permissions
            .HasPermissionAsync(user, Permission.AdhocActionsExecute, new PermissionScope(), ct: ct)
            .ConfigureAwait(false);
        if (!allowed)
        {
            var who = user.Identity?.Name ?? "(unknown)";
            await McpAudit.ToolInvokedAsync(audit, toolName, $"user={who}", "permission-denied", ct)
                .ConfigureAwait(false);
            throw new McpException(
                "Caller does not have Permission.AdhocActionsExecute. Ad-hoc actions are " +
                "the single-approver-gated surface (M11.E.5); a separate operator may need " +
                "to grant your API key the AdhocActionsExecute permission first.");
        }

        var userIdRaw = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var userId = Guid.TryParse(userIdRaw, out var u) ? u : Guid.Empty;
        var display = user.Identity?.Name ?? "mcp-client";
        return (userId, display);
    }

    private static AdhocMode ParseMode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return AdhocMode.Readonly;
        }
        return raw.Trim().ToLowerInvariant() switch
        {
            "readonly" => AdhocMode.Readonly,
            "mutating" => AdhocMode.Mutating,
            _ => throw new McpException(
                $"Invalid mode '{raw}'. Use 'readonly' or 'mutating'."),
        };
    }

    private static AdhocIterationDto MapIteration(AdhocIteration i)
        => new(
            IterationId:    i.Id,
            IterNumber:     i.IterNumber,
            Status:         i.Status.ToString(),
            Verdict:        i.Verdict == AdhocVerdict.Pending ? null : i.Verdict.ToString(),
            Description:    i.Description,
            RiskAssessment: i.RiskAssessment,
            Script:         i.GeneratedScript,
            Narrative:      i.Narrative,
            ApprovedBy:     i.ApprovedByDisplay,
            ApprovedAtUtc:  i.ApprovedAtUtc,
            Results:        SafeDeserialiseResults(i.ResultsJson));

    private static List<AdhocResultDto> SafeDeserialiseResults(string json)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<List<AdhocPerTargetResult>>(json, JsonOpts) ?? [];
            return raw.Select(r => new AdhocResultDto(
                TargetId:   r.TargetId,
                ExitCode:   r.Result.ExitCode,
                Stdout:     r.Result.Stdout,
                Stderr:     r.Result.Stderr,
                AgentError: r.Result.AgentError)).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web);
}

/// <summary>Result of <c>run_adhoc_action</c> — what the MCP caller gets back
/// after the server has created the session + generated iteration 1 + run
/// the static-analysis gate.</summary>
public sealed record AdhocInitiationResult(
    Guid SessionId,
    Guid IterationId,
    string ApprovalUrl,
    string Prompt,
    string Mode,
    IReadOnlyList<Guid> FrozenTargetIds,
    string ProposedScript,
    string Description,
    string RiskAssessment,
    string ExpectedOutputShape,
    bool RequiresMutation,
    bool ApprovalPending,
    string HumanApprovalRequiredNote);

/// <summary>Result of <c>get_adhoc_session</c> — current state of a session
/// with all its iterations.</summary>
public sealed record AdhocSessionDetailDto(
    Guid SessionId,
    string Status,
    string Mode,
    string Prompt,
    int MaxIterations,
    string CreatedByUser,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<AdhocIterationDto> Iterations);

/// <summary>Per-iteration projection for <see cref="AdhocSessionDetailDto"/>.</summary>
public sealed record AdhocIterationDto(
    Guid IterationId,
    int IterNumber,
    string Status,
    string? Verdict,
    string Description,
    string RiskAssessment,
    string Script,
    string Narrative,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAtUtc,
    IReadOnlyList<AdhocResultDto> Results);

/// <summary>Per-target result projection.</summary>
public sealed record AdhocResultDto(
    Guid TargetId,
    int ExitCode,
    string Stdout,
    string Stderr,
    string? AgentError);
