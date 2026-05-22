using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Ai;

/// <summary>
/// Audit row for one AI completion call (M11.A.3). Written by
/// <c>KrakenDeploy.Ai.IKrakenAi</c> after every LLM round-trip, success or
/// failure. Drives the cost-per-Space reporting + per-feature usage view.
/// <para>
/// Space-scoped so the cost report attributes spend correctly to whoever's
/// LLM keys + monthly budget got consumed. Auditable (<see cref="CreatedUtc"/>
/// + <see cref="ModifiedUtc"/>) for the existing audit interceptor pipeline.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <strong>Body columns (<see cref="PromptBodyJson"/>, <see cref="ResponseBody"/>):</strong>
/// nullable, populated only when the Space's <c>Ai:LogPromptBodies</c>
/// setting is <c>true</c>. The default is <c>null</c> on every row — the
/// audit table has a juicy GDPR footprint with bodies stored, so opt-in
/// only.
/// </para>
/// <para>
/// <strong>Cost (<see cref="CostUsd"/>):</strong> computed from
/// (input + output tokens) × the provider's per-1k rate at the time of the
/// call, embedded in source via the rate table that lands with M11.A.5. We
/// snapshot the cost on insert rather than computing on read so historical
/// rows stay accurate after provider price changes.
/// </para>
/// </remarks>
public class AiCallLog : AuditableEntity, ISpaceScoped
{
    /// <summary>FK to the owning Space. Auto-stamped by the SpaceScopingInterceptor.</summary>
    public Guid SpaceId { get; set; }

    /// <summary>
    /// String of the <c>KrakenAiProvider</c> enum (e.g. <c>"Anthropic"</c>,
    /// <c>"AzureOpenAI"</c>). Stored as text so older rows survive enum
    /// renames without a backfill migration.
    /// </summary>
    public required string Provider { get; set; }

    /// <summary>Model id at call time (e.g. <c>claude-3-5-sonnet-20241022</c>).</summary>
    public required string Model { get; set; }

    /// <summary>
    /// String of the <c>KrakenAiFeature</c> enum: <c>Diagnosis</c>,
    /// <c>Adhoc</c>, <c>Assistant</c>, <c>Mcp</c>. Drives the per-feature
    /// usage rollup in the admin UI.
    /// </summary>
    public required string Feature { get; set; }

    /// <summary>
    /// Number of input tokens billed (provider-reported). Zero on failure
    /// when the provider didn't return usage stats.
    /// </summary>
    public int PromptTokens { get; set; }

    /// <summary>Number of output tokens billed. Zero on failure.</summary>
    public int CompletionTokens { get; set; }

    /// <summary>Wall-clock latency of the call in milliseconds.</summary>
    public int LatencyMs { get; set; }

    /// <summary>
    /// USD cost of this call, computed at insert time. Cached so historical
    /// rows stay accurate after provider price changes. Zero when the
    /// provider's pricing isn't known to the rate table — surfaced in the
    /// admin UI so the operator can correct the gap.
    /// </summary>
    public decimal CostUsd { get; set; }

    /// <summary><c>true</c> on a successful round-trip.</summary>
    public bool Success { get; set; }

    /// <summary>
    /// Exception type + message on failure, redacted of API keys. <c>null</c>
    /// on success. Useful for diagnosing provider outages, quota errors,
    /// authentication regressions.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Optional caller-supplied correlation id grouping multiple calls
    /// under one logical operation (e.g. all iterations of one adhoc
    /// session, every retry of a diagnosis attempt).
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// User who triggered the call when known (UI-initiated paths). <c>null</c>
    /// for background jobs (Hangfire-triggered diagnosis, MCP-triggered
    /// actions where the MCP layer doesn't carry a Kraken user id).
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// Sanitisation marker (set by M11.A.4): comma-separated names of
    /// <c>Sensitive</c>-flagged variables that were stripped from the
    /// prompt before transmission. Names only, never values. <c>null</c>
    /// when nothing was scrubbed.
    /// </summary>
    public string? ScrubbedVariableNames { get; set; }

    /// <summary>
    /// Full prompt body (<c>jsonb</c>), populated only when the Space's
    /// <c>LogPromptBodies</c> is on. Stored as the serialised
    /// <c>ChatMessage[]</c> the wrapper sent into the provider.
    /// </summary>
    public string? PromptBodyJson { get; set; }

    /// <summary>
    /// Full response text, populated only when <c>LogPromptBodies</c> is on.
    /// </summary>
    public string? ResponseBody { get; set; }
}
