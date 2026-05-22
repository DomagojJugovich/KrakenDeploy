using Microsoft.Extensions.AI;

namespace KrakenDeploy.Ai;

/// <summary>
/// KrakenDeploy's narrow wrapper around <c>Microsoft.Extensions.AI.IChatClient</c>
/// (M11.A.1). Every AI call across the codebase goes through this surface so
/// the audit, sanitisation, and budget pipeline runs uniformly regardless of
/// provider or feature.
/// <para>
/// Callers identify themselves via <see cref="KrakenAiFeature"/> so audit
/// rows attribute cost + token consumption to the right sub-feature
/// (diagnosis, ad-hoc, assistant, MCP).
/// </para>
/// <para>
/// All methods throw <see cref="KrakenAiDisabledException"/> when the
/// Space's provider is <see cref="KrakenAiProvider.Disabled"/>,
/// <see cref="KrakenAiFeatureDisabledException"/> when the per-feature
/// flag is off, and <see cref="KrakenAiBudgetExceededException"/> when
/// month-to-date cost has reached the cap. Callers MUST handle these as
/// "feature not available" rather than as runtime failures — particularly
/// in M11.C (diagnosis), where a missing AI must not affect deployment status.
/// </para>
/// </summary>
public interface IKrakenAi
{
    /// <summary>
    /// One-shot completion of <paramref name="messages"/>. Returns the full
    /// response. Tokens, latency, and (optionally) prompt + response bodies
    /// land in the <c>AiCallLog</c> table.
    /// </summary>
    Task<KrakenAiCompletion> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        KrakenAiFeature            feature,
        KrakenAiRequestOptions?    options = null,
        CancellationToken          ct      = default);

    /// <summary>
    /// One-shot completion expecting JSON output that deserialises to
    /// <typeparamref name="TResult"/>. Uses the provider's structured-output
    /// mode (Anthropic tool use / OpenAI <c>response_format=json_schema</c>)
    /// so the model is constrained to valid JSON of the requested shape.
    /// </summary>
    Task<TResult> CompleteAsync<TResult>(
        IReadOnlyList<ChatMessage> messages,
        KrakenAiFeature            feature,
        KrakenAiRequestOptions?    options = null,
        CancellationToken          ct      = default)
        where TResult : class;

    /// <summary>
    /// Streaming completion — yields response chunks as the provider emits
    /// them. Used by the process-builder assistant (M11.D) for "typing"
    /// feedback in the script editor sidebar.
    /// </summary>
    IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        KrakenAiFeature            feature,
        KrakenAiRequestOptions?    options = null,
        CancellationToken          ct      = default);
}

/// <summary>
/// Identifies which Kraken sub-feature is making the AI call. Used for
/// audit attribution + per-feature flag enforcement.
/// </summary>
public enum KrakenAiFeature
{
    /// <summary>M11.C — autonomous failure diagnosis.</summary>
    Diagnosis = 0,

    /// <summary>M11.E — ad-hoc agent actions (script generation + iteration).</summary>
    Adhoc = 1,

    /// <summary>M11.D — process-builder assistant UI (inline suggestions, field explanations).</summary>
    Assistant = 2,

    /// <summary>M11.B — MCP server (when the MCP host itself calls the LLM, rare).</summary>
    Mcp = 3,
}

/// <summary>Optional per-call knobs.</summary>
public sealed record KrakenAiRequestOptions
{
    /// <summary>Sampling temperature (0.0–2.0). Provider-clamped if needed.</summary>
    public float? Temperature { get; init; }

    /// <summary>Max tokens in the response. <c>null</c> = provider default.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Free-form correlation id surfaced in the <c>AiCallLog</c> row. Useful
    /// when a feature wants to group multiple AI calls under one operation
    /// (e.g. all iterations of one adhoc session).
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Map of variable name → sensitive value that the wrapper passes to
    /// <see cref="IPromptSanitizer"/> before sending the prompt to the LLM
    /// (M11.A.4). Each value found in any message's text is replaced with
    /// <c>[REDACTED:&lt;name&gt;]</c>; the names of substituted variables
    /// land in the audit row's <c>ScrubbedVariableNames</c> column.
    /// <para>
    /// <c>null</c> = no sanitisation (the wrapper passes messages through
    /// verbatim). Callers in features that ever touch deployment variables
    /// (M11.C diagnosis, M11.E adhoc, M11.D assistant) MUST populate this
    /// when the prompt contains any user-supplied value — the sanitiser
    /// is the last line of defence against credential exfiltration.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string>? SensitiveValues { get; init; }
}

/// <summary>Result of a one-shot completion.</summary>
public sealed record KrakenAiCompletion(
    string Text,
    int PromptTokens,
    int CompletionTokens,
    TimeSpan Latency,
    string Provider,
    string Model);
