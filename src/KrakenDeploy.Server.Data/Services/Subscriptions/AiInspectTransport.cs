using System.Text;
using System.Text.Json;
using KrakenDeploy.Ai;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Subscriptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services.Subscriptions;

/// <summary>
/// M11.C closes here. A matching event is sent to the LLM with a prompt
/// asking for a diagnosis / next-steps summary; the model's response
/// becomes a new audit row of type <c>Diagnosis.Completed</c>, which is
/// itself subscribable — so an operator can chain "diagnose Deployment.Failed,
/// then post the diagnosis to Slack" by stacking two subscriptions
/// (this transport on the first, the Webhook transport on the second
/// listening for <c>Diagnosis.*</c>).
///
/// <para>
/// The transport tolerates AI being disabled / over-budget / mis-configured
/// by returning a failure result — the dispatcher's row write captures
/// the reason. The same Space-level AI feature flags M11.A.6 introduced
/// (<c>DiagnosisEnabled</c> bool on <c>SpaceAiSettings</c>) gate access
/// at the <c>IKrakenAi</c> layer; this transport just calls through.
/// </para>
/// </summary>
public sealed class AiInspectTransport(
    IKrakenAi ai,
    IAuditLog auditLog,
    ILogger<AiInspectTransport> logger) : IEventTransport
{
    public SubscriptionTransport Transport => SubscriptionTransport.AiInspect;

    private static readonly JsonSerializerOptions ConfigJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Default prompt template — operator can override per
    /// subscription via the transport config's <c>prompt</c> field. The
    /// template is plain text; <c>{EventType}</c> / <c>{Details}</c> /
    /// <c>{Subject}</c> placeholders get string-substituted before the
    /// model sees the prompt.</summary>
    internal const string DefaultPrompt =
        "An event occurred in KrakenDeploy that may indicate a problem. " +
        "Read the event payload and provide a concise (3–6 sentence) " +
        "operator-facing diagnosis: what likely happened, the most " +
        "probable cause, and one or two concrete next-step actions an " +
        "operator should consider. Avoid speculation when the payload " +
        "is sparse — say so explicitly.\n\n" +
        "Event type: {EventType}\n" +
        "Subject: {Subject}\n" +
        "Details: {Details}";

    /// <summary>Cap on the diagnosis length we persist as a Diagnosis.Completed
    /// audit event. The model output is also length-bounded via
    /// <c>MaxOutputTokens</c>, but a defensive truncation keeps the audit
    /// row size bounded if the model ignores the limit.</summary>
    internal const int MaxStoredDiagnosisChars = 4000;

    public async Task<EventTransportResult> DeliverAsync(
        EventSubscription subscription,
        AuditEntry auditEvent,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(auditEvent);

        // Build the prompt. Config is optional — empty {} = use default.
        AiInspectConfig config;
        try
        {
            config = JsonSerializer.Deserialize<AiInspectConfig>(
                subscription.TransportConfigJson, ConfigJsonOpts)
                ?? new AiInspectConfig();
        }
        catch (Exception ex)
        {
            return EventTransportResult.Failure(
                $"Malformed AI transport config: {ex.Message}");
        }

        var template = string.IsNullOrWhiteSpace(config.Prompt)
            ? DefaultPrompt
            : config.Prompt;
        var prompt = FillTemplate(template, auditEvent);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are an SRE assistant inspecting deployment-orchestrator events."),
            new(ChatRole.User,   prompt),
        };

        try
        {
            var completion = await ai.CompleteAsync(
                messages,
                KrakenAiFeature.Diagnosis,
                new KrakenAiRequestOptions
                {
                    Temperature     = 0.2f,
                    MaxOutputTokens = 600,
                    CorrelationId   = $"event:{auditEvent.Id:N}",
                },
                ct).ConfigureAwait(false);

            // Truncate before persisting — defensive against runaway output.
            var diagnosis = completion.Text.Length > MaxStoredDiagnosisChars
                ? completion.Text[..MaxStoredDiagnosisChars] + "…"
                : completion.Text;

            // The diagnosis becomes a new audit event so it's itself
            // subscribable — chain two subscriptions to get "diagnose,
            // then post to webhook".
            await auditLog.RecordAsync(
                eventType:   AuditEventType.DiagnosisCompleted,
                subjectType: "AuditEntry",
                subjectId:   auditEvent.Id.ToString(),
                subjectName: auditEvent.EventType,
                details:     diagnosis,
                ct: ct).ConfigureAwait(false);

            logger.LogInformation(
                "AI diagnosis ok: sub={SubId} event={EventId} tokens={Prompt}+{Completion} latency={Latency}",
                subscription.Id, auditEvent.Id,
                completion.PromptTokens, completion.CompletionTokens, completion.Latency);

            return EventTransportResult.Success(
                $"Diagnosis recorded ({completion.PromptTokens}+{completion.CompletionTokens} tokens, " +
                $"{completion.Latency.TotalMilliseconds:F0} ms, {completion.Provider}/{completion.Model}).");
        }
        catch (KrakenAiDisabledException)
        {
            return EventTransportResult.Failure(
                "AI is disabled for this Space — turn on the provider in " +
                "Settings → AI before relying on this transport.");
        }
        catch (KrakenAiFeatureDisabledException)
        {
            return EventTransportResult.Failure(
                "Diagnosis is disabled for this Space — enable it in " +
                "Settings → AI before this subscription can fire.");
        }
        catch (KrakenAiBudgetExceededException ex)
        {
            return EventTransportResult.Failure(
                $"AI budget exceeded: {ex.Message}");
        }
        catch (Exception ex)
        {
            return EventTransportResult.Failure(
                $"AI call failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Substitutes <c>{EventType}</c>, <c>{Subject}</c>, <c>{Details}</c>,
    /// <c>{OccurredUtc}</c> placeholders in the prompt template. Unknown
    /// placeholders are left untouched — no escaping needed because the
    /// substitution targets are short strings the operator wrote, not
    /// arbitrary user input.
    /// </summary>
    internal static string FillTemplate(string template, AuditEntry e)
    {
        var sb = new StringBuilder(template);
        sb.Replace("{EventType}",   e.EventType ?? "(unknown)");
        sb.Replace("{Subject}",
            e.SubjectName
            ?? (e.SubjectType is not null && e.SubjectId is not null
                ? $"{e.SubjectType}/{e.SubjectId}"
                : "(none)"));
        sb.Replace("{Details}",     e.Details ?? "(empty)");
        sb.Replace("{OccurredUtc}", e.OccurredUtc.ToString("O"));
        return sb.ToString();
    }

    /// <summary>Schema for the subscription's TransportConfigJson when
    /// Transport=AiInspect. All fields optional; defaults apply.</summary>
    internal sealed record AiInspectConfig(string? Prompt = null);
}
