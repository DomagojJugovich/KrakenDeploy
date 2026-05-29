using System.Text;
using System.Text.Json;
using KrakenDeploy.Ai;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Server.Core.Domain.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services.Ai.Adhoc;

/// <summary>
/// M11.E.13 — after an iteration's per-target results stream back, this
/// service makes the SECOND LLM call: feeds <c>{ originalPrompt, mode,
/// priorIteration.script, perTargetResults }</c> into a verdict prompt and
/// returns the structured <see cref="IterationVerdict"/>. The orchestrator
/// (<c>AdhocSessionService</c>) uses the verdict to decide whether to close
/// the session (<see cref="AdhocVerdict.AllSucceeded"/> /
/// <see cref="AdhocVerdict.NoFixAvailable"/>) or open the next iteration's
/// approval dialog (<see cref="AdhocVerdict.ProposeFix"/>).
/// <para>
/// Same unavailability translation as <see cref="AdhocGenerationService"/>:
/// every <see cref="IKrakenAi"/>-disabled / budget-exceeded throw is mapped
/// to a typed <see cref="AdhocFeatureUnavailableException"/> so the API layer
/// can return a clean 503/422.
/// </para>
/// </summary>
public sealed class AdhocVerdictService(
    IKrakenAi ai,
    ILogger<AdhocVerdictService> logger)
{
    private const string SystemPromptHeader =
        "You are a senior site-reliability engineer reviewing the per-target results " +
        "of a PowerShell script just executed on a frozen set of Windows servers. " +
        "Decide whether the original operator request is fulfilled, cannot be fixed, " +
        "or needs one more attempt. " +
        "If you propose a fix script, it MUST be idempotent (re-running on already-" +
        "fixed targets must be safe), self-contained (no remoting), and stay within " +
        "the session's mode — a readonly session NEVER gets a mutating fix. " +
        "Do NOT propose changing the target set; you have no field to do so and the " +
        "fix runs on the SAME frozen set as the prior iteration. " +
        "Output ONLY the JSON shape requested.";

    /// <summary>
    /// Evaluates <paramref name="iteration"/>'s results and returns the
    /// verdict. <paramref name="iteration"/>'s <c>ResultsJson</c> must
    /// already be populated.
    /// </summary>
    public async Task<IterationVerdict> EvaluateAsync(
        AdhocSession session,
        AdhocIteration iteration,
        IReadOnlyList<AdhocScriptResult> results,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(iteration);
        ArgumentNullException.ThrowIfNull(results);

        var systemPrompt = BuildSystemPrompt(session.Mode);
        var userPrompt   = BuildUserPrompt(session, iteration, results);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User,   userPrompt),
        };

        try
        {
            return await ai.CompleteAsync<IterationVerdict>(
                messages,
                KrakenAiFeature.Adhoc,
                new KrakenAiRequestOptions
                {
                    Temperature     = 0.2f,
                    MaxOutputTokens = 1200,
                    CorrelationId   = $"adhoc-verdict:{session.Id:N}:iter{iteration.IterNumber}",
                },
                ct).ConfigureAwait(false);
        }
        catch (KrakenAiDisabledException ex)
        {
            logger.LogInformation(
                "Adhoc verdict refused for session {SessionId} iter {Iter}: provider disabled.",
                session.Id, iteration.IterNumber);
            throw new AdhocFeatureUnavailableException(
                AdhocFeatureUnavailableReason.ProviderDisabled, ex.Message, ex);
        }
        catch (KrakenAiFeatureDisabledException ex)
        {
            logger.LogInformation(
                "Adhoc verdict refused for session {SessionId} iter {Iter}: Adhoc flag off.",
                session.Id, iteration.IterNumber);
            throw new AdhocFeatureUnavailableException(
                AdhocFeatureUnavailableReason.FeatureDisabled, ex.Message, ex);
        }
        catch (KrakenAiBudgetExceededException ex)
        {
            logger.LogWarning(
                "Adhoc verdict refused for session {SessionId} iter {Iter}: budget exceeded.",
                session.Id, iteration.IterNumber);
            throw new AdhocFeatureUnavailableException(
                AdhocFeatureUnavailableReason.BudgetExceeded, ex.Message, ex);
        }
    }

    /// <summary>
    /// Parses the LLM's <see cref="IterationVerdict.Verdict"/> string into
    /// the persisted <see cref="AdhocVerdict"/> enum. Unknown values map to
    /// <see cref="AdhocVerdict.NoFixAvailable"/> (closes the session safely)
    /// rather than throwing — the model's output stays inert if it ever
    /// deviates from the prompt.
    /// </summary>
    public static AdhocVerdict ParseVerdict(string raw)
        => raw switch
        {
            "AllSucceeded"  => AdhocVerdict.AllSucceeded,
            "NoFixAvailable" => AdhocVerdict.NoFixAvailable,
            "ProposeFix"    => AdhocVerdict.ProposeFix,
            _               => AdhocVerdict.NoFixAvailable,
        };

    private static string BuildSystemPrompt(AdhocMode mode)
    {
        var sb = new StringBuilder(SystemPromptHeader);
        sb.Append(mode == AdhocMode.Readonly
            ? " The session is READONLY: any ProposedScript MUST use only " +
              "Get-/Test-/Measure-* and safe utility cmdlets — no state changes."
            : " The session is MUTATING: a ProposedScript may change state but must " +
              "avoid Remove-Item -Recurse -Force, registry-write cmdlets, service " +
              "install/uninstall, Add-Type, and remoting.");
        return sb.ToString();
    }

    private static string BuildUserPrompt(
        AdhocSession session, AdhocIteration iteration,
        IReadOnlyList<AdhocScriptResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Original operator request");
        sb.AppendLine(session.Prompt);
        sb.AppendLine();
        sb.Append("Mode: ").AppendLine(session.Mode.ToString());
        sb.Append("Iteration: ").AppendLine(iteration.IterNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        sb.AppendLine();
        sb.AppendLine("# Script just executed");
        sb.AppendLine("```powershell");
        sb.AppendLine(iteration.GeneratedScript);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.Append("# Per-target results (").Append(results.Count).AppendLine(" targets)");
        var resultsJson = JsonSerializer.Serialize(results, JsonOptions);
        sb.AppendLine(resultsJson);
        sb.AppendLine();
        sb.AppendLine("Decide: AllSucceeded / NoFixAvailable / ProposeFix.");
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };
}
