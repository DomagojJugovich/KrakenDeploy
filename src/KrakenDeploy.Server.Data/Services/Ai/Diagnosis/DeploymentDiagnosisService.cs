using System.Text.Json;
using KrakenDeploy.Ai;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services.Ai.Diagnosis;

/// <summary>
/// M11.C — runs an autonomous AI diagnosis of a failed deployment: assembles
/// the failure context, calls the LLM with a structured-output schema,
/// persists a <see cref="DeploymentDiagnosis"/> (upsert by deployment), and
/// records a <c>Diagnosis.Completed</c> audit event (the same event the
/// M13.B AiInspect transport emits, so existing "diagnose → notify"
/// subscription chains keep working).
/// <para>
/// <strong>Best-effort by contract.</strong> A disabled / over-budget /
/// mis-configured AI provider, or any transient LLM error, must NEVER affect
/// deployment status — the deployment already failed for its own reasons.
/// Every AI exception is caught + logged; the method returns without
/// persisting. The caller (the diagnosis worker) treats a thrown exception
/// as "diagnosis unavailable", not as a deployment problem.
/// </para>
/// </summary>
public sealed class DeploymentDiagnosisService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    DiagnosisContextAssembler assembler,
    IKrakenAi ai,
    IAuditLog auditLog,
    ILogger<DeploymentDiagnosisService> logger)
{
    private const string SystemPrompt =
        "You are a senior SRE diagnosing a failed software deployment. You are " +
        "given the deployment's failed steps, what changed since the last " +
        "successful run, target health, and the tail of the deployment log. " +
        "Produce a concise, actionable diagnosis. Prefer the most probable " +
        "single cause over a list of possibilities. When the evidence is thin, " +
        "say so and set confidence Low. Never invent log content; only " +
        "reference lines that appear in the provided log.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Diagnoses <paramref name="deploymentId"/>. Silently no-ops when the
    /// deployment is unknown or has no log (nothing to diagnose). Swallows
    /// AI-unavailable + transient errors (logged).
    /// </summary>
    public async Task DiagnoseAsync(Guid deploymentId, CancellationToken ct = default)
    {
        AssembledContextEnvelope envelope;
        try
        {
            var assembled = await assembler.AssembleAsync(deploymentId, ct).ConfigureAwait(false);
            if (assembled is null)
            {
                logger.LogDebug(
                    "Diagnosis skipped for {DeploymentId}: deployment not found / no log.", deploymentId);
                return;
            }
            envelope = new AssembledContextEnvelope(assembled.PromptBody, assembled.SensitiveValues);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Diagnosis context assembly failed for {DeploymentId}.", deploymentId);
            return;
        }

        DiagnosisResult result;
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, envelope.PromptBody),
            };
            result = await ai.CompleteAsync<DiagnosisResult>(
                messages,
                KrakenAiFeature.Diagnosis,
                new KrakenAiRequestOptions
                {
                    Temperature     = 0.2f,
                    MaxOutputTokens = 800,
                    CorrelationId   = $"diagnosis:{deploymentId:N}",
                    SensitiveValues = envelope.SensitiveValues,
                },
                ct).ConfigureAwait(false);
        }
        catch (KrakenAiDisabledException)
        {
            logger.LogInformation(
                "Diagnosis skipped for {DeploymentId}: AI provider is disabled for the Space.", deploymentId);
            return;
        }
        catch (KrakenAiFeatureDisabledException)
        {
            logger.LogInformation(
                "Diagnosis skipped for {DeploymentId}: the Diagnosis feature flag is off.", deploymentId);
            return;
        }
        catch (KrakenAiBudgetExceededException ex)
        {
            logger.LogWarning(
                "Diagnosis skipped for {DeploymentId}: AI budget exceeded — {Message}", deploymentId, ex.Message);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Diagnosis LLM call failed for {DeploymentId}.", deploymentId);
            return;
        }

        await PersistAsync(deploymentId, result, ct).ConfigureAwait(false);
    }

    private async Task PersistAsync(Guid deploymentId, DiagnosisResult result, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Resolve the deployment's Space so the row is stamped correctly —
        // the worker runs without an HTTP space context, so we can't rely on
        // the SpaceScopingInterceptor's ambient default.
        var spaceId = await db.Deployments.IgnoreQueryFilters()
            .Where(d => d.Id == deploymentId)
            .Select(d => (Guid?)d.SpaceId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (spaceId is null)
        {
            logger.LogWarning(
                "Diagnosis not persisted: deployment {DeploymentId} vanished before write.", deploymentId);
            return;
        }

        var confidence = Enum.TryParse<DiagnosisConfidence>(result.Confidence, ignoreCase: true, out var c)
            ? c
            : DiagnosisConfidence.Low;
        var logLinesJson = JsonSerializer.Serialize(
            result.RelevantLogLines.Select(l => new { sequence = l.Sequence, text = l.Text }), Json);

        var existing = await db.DeploymentDiagnoses.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.DeploymentId == deploymentId, ct).ConfigureAwait(false);

        if (existing is null)
        {
            db.DeploymentDiagnoses.Add(new DeploymentDiagnosis
            {
                SpaceId              = spaceId.Value,
                DeploymentId         = deploymentId,
                ProbableCause        = Trim(result.ProbableCause, 2000),
                Confidence           = confidence,
                SuggestedFix         = Trim(result.SuggestedFix, 2000),
                RelevantLogLinesJson = logLinesJson,
                // Token/model attribution lives in AiCallLog (correlated by deployment id).
            });
        }
        else
        {
            existing.ProbableCause        = Trim(result.ProbableCause, 2000);
            existing.Confidence           = confidence;
            existing.SuggestedFix         = Trim(result.SuggestedFix, 2000);
            existing.RelevantLogLinesJson = logLinesJson;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Diagnosis.Completed audit — subscribable, so "diagnose then notify"
        // chains (M13.B) fire off this too.
        await auditLog.RecordAsync(
            AuditEventType.DiagnosisCompleted,
            subjectType: "Deployment",
            subjectId:   deploymentId.ToString(),
            details:     $"Confidence={confidence}; {Trim(result.ProbableCause, 500)}",
            ct: ct).ConfigureAwait(false);

        logger.LogInformation(
            "Diagnosis recorded for {DeploymentId}: confidence={Confidence}.", deploymentId, confidence);
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max];

    private sealed record AssembledContextEnvelope(
        string PromptBody, IReadOnlyDictionary<string, string> SensitiveValues);
}
