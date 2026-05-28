using System.Text;
using KrakenDeploy.Ai;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Targets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services.Ai.Adhoc;

/// <summary>
/// M11.E.2 — the generation pipeline: builds the system + user prompt with the
/// session's target context, calls the LLM in structured-output mode via
/// <see cref="IKrakenAi.CompleteAsync{TResult}"/>, and returns the parsed
/// <see cref="AdhocGenerationResult"/>. Used for iteration 1 (turn 1) of an
/// ad-hoc session; the verdict / propose-fix call in subsequent iterations
/// lives in <c>AdhocIterationService</c> (M11.E.13, commit 5).
/// <para>
/// <strong>Feature unavailability is not a failure.</strong> When the AI
/// wrapper throws <see cref="KrakenAiDisabledException"/>,
/// <see cref="KrakenAiFeatureDisabledException"/>, or
/// <see cref="KrakenAiBudgetExceededException"/>, the service rethrows them as
/// <see cref="AdhocFeatureUnavailableException"/> with a short reason so the
/// API layer can return a clean 503 / 422 with a human-readable message rather
/// than a generic 500. Transient LLM errors propagate to the caller.
/// </para>
/// <para>
/// <strong>Sanitisation.</strong> Sensitive variables present in the target
/// context (or supplied by the caller) are passed via
/// <see cref="KrakenAiRequestOptions.SensitiveValues"/> so
/// <c>IPromptSanitizer</c> redacts them before the request leaves the
/// process (M11.A.4). Callers MUST pass the Space's resolved sensitive
/// variables when relevant.
/// </para>
/// </summary>
public sealed class AdhocGenerationService(
    IKrakenAi ai,
    ILogger<AdhocGenerationService> logger)
{
    private const string SystemPromptHeader =
        "You are a senior site-reliability engineer generating a PowerShell script " +
        "that runs locally on a Windows server via an installed KrakenDeploy agent. " +
        "You DO NOT have shell access — your script will be sent to operators for " +
        "approval, signed, and then executed locally on each target in a frozen set " +
        "the operator picked. You cannot change the target set and you cannot add " +
        "more targets later. " +
        "Output ONLY the JSON shape requested. Keep the script minimal, idempotent, " +
        "and self-contained. Use Write-Host for human-readable output. Do not use " +
        "remoting (Invoke-Command -ComputerName / -Session) — the agent already " +
        "runs ON the target. Do not use Invoke-Expression or Add-Type — both will " +
        "be rejected by the static-analysis gate. ";

    /// <summary>
    /// Generates a script for iteration 1 of <paramref name="session"/>.
    /// <paramref name="resolvedTargets"/> MUST be the persisted frozen set
    /// rehydrated from <see cref="AdhocSession.FrozenTargetSetJson"/> — the
    /// caller resolves target ids → <see cref="DeploymentTarget"/> rows so the
    /// service stays free of the DB.
    /// </summary>
    /// <param name="sensitiveValues">
    /// Optional sensitive-value map (variable name → secret) for
    /// <c>IPromptSanitizer</c> redaction. <c>null</c> when no sensitive
    /// variables are exposed in the prompt body.
    /// </param>
    public async Task<AdhocGenerationResult> GenerateAsync(
        AdhocSession session,
        IReadOnlyList<DeploymentTarget> resolvedTargets,
        IReadOnlyDictionary<string, string>? sensitiveValues,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(resolvedTargets);

        var systemPrompt = BuildSystemPrompt(session.Mode);
        var userPrompt   = BuildUserPrompt(session, resolvedTargets);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User,   userPrompt),
        };

        try
        {
            return await ai.CompleteAsync<AdhocGenerationResult>(
                messages,
                KrakenAiFeature.Adhoc,
                new KrakenAiRequestOptions
                {
                    // Low temperature: we want predictable, conservative scripts.
                    Temperature     = 0.2f,
                    MaxOutputTokens = 1200,
                    CorrelationId   = $"adhoc-gen:{session.Id:N}:iter1",
                    SensitiveValues = sensitiveValues,
                },
                ct).ConfigureAwait(false);
        }
        catch (KrakenAiDisabledException ex)
        {
            logger.LogInformation(
                "Ad-hoc generation refused for session {SessionId}: provider disabled.",
                session.Id);
            throw new AdhocFeatureUnavailableException(
                AdhocFeatureUnavailableReason.ProviderDisabled, ex.Message, ex);
        }
        catch (KrakenAiFeatureDisabledException ex)
        {
            logger.LogInformation(
                "Ad-hoc generation refused for session {SessionId}: Adhoc feature flag off.",
                session.Id);
            throw new AdhocFeatureUnavailableException(
                AdhocFeatureUnavailableReason.FeatureDisabled, ex.Message, ex);
        }
        catch (KrakenAiBudgetExceededException ex)
        {
            logger.LogWarning(
                "Ad-hoc generation refused for session {SessionId}: budget exceeded.",
                session.Id);
            throw new AdhocFeatureUnavailableException(
                AdhocFeatureUnavailableReason.BudgetExceeded, ex.Message, ex);
        }
    }

    private static string BuildSystemPrompt(AdhocMode mode)
    {
        var sb = new StringBuilder(SystemPromptHeader);
        if (mode == AdhocMode.Readonly)
        {
            sb.Append("This session is READONLY. Use only Get-*/Test-*/Measure-* cmdlets " +
                      "plus standard pipeline utilities (Select-Object, Where-Object, " +
                      "Sort-Object, Format-*, ForEach-Object, ConvertTo/From-Json). Anything " +
                      "that mutates state will be rejected by the gate. Set RequiresMutation=false.");
        }
        else
        {
            sb.Append("This session is MUTATING. State changes are permitted, but avoid: " +
                      "Remove-Item -Recurse -Force, registry-write cmdlets, service install/" +
                      "uninstall (New-Service / Remove-Service). Prefer idempotent operations " +
                      "(check current state, then change only if needed). Set RequiresMutation=true " +
                      "if the script actually changes state.");
        }
        return sb.ToString();
    }

    private static string BuildUserPrompt(
        AdhocSession session, IReadOnlyList<DeploymentTarget> targets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Operator request");
        sb.AppendLine(session.Prompt);
        sb.AppendLine();
        sb.AppendLine("# Session context");
        sb.Append("Mode: ").AppendLine(session.Mode.ToString());
        sb.Append("Frozen target set (").Append(targets.Count).AppendLine(" targets):");
        if (targets.Count == 0)
        {
            sb.AppendLine("  (none — should never reach generation; check the resolver.)");
        }
        else
        {
            foreach (var t in targets)
            {
                var os    = string.IsNullOrWhiteSpace(t.OperatingSystem) ? "unknown OS" : t.OperatingSystem;
                var roles = t.Roles.Count == 0 ? "no roles" : string.Join(", ", t.Roles);
                sb.Append("  - ").Append(t.Name)
                  .Append(" [").Append(os).Append(']')
                  .Append(" [").Append(roles).Append(']')
                  .Append(" [status=").Append(t.Status).Append(']')
                  .AppendLine();
            }
        }
        sb.AppendLine();
        sb.AppendLine("Produce a script that the static-analysis gate will accept for this mode, " +
                      "answering the operator request against the frozen target set above.");
        return sb.ToString();
    }
}

/// <summary>
/// Thrown by <see cref="AdhocGenerationService"/> (and later iteration calls)
/// when the AI feature isn't available — wraps the underlying
/// <see cref="IKrakenAi"/> exception so the API + UI layer can map it to a
/// clean 503 / 422 with a human-readable reason rather than a generic 500.
/// </summary>
public sealed class AdhocFeatureUnavailableException : Exception
{
    public AdhocFeatureUnavailableReason Reason { get; }

    public AdhocFeatureUnavailableException(
        AdhocFeatureUnavailableReason reason, string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }
}

/// <summary>Classification of an <see cref="AdhocFeatureUnavailableException"/>.</summary>
public enum AdhocFeatureUnavailableReason
{
    /// <summary>The Space's AI provider is set to Disabled.</summary>
    ProviderDisabled = 0,

    /// <summary>The per-Space <c>AdhocEnabled</c> flag is off.</summary>
    FeatureDisabled = 1,

    /// <summary>Month-to-date AI cost has reached the Space's cap.</summary>
    BudgetExceeded = 2,
}
