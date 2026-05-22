namespace KrakenDeploy.Ai;

/// <summary>
/// Sink that persists one row per AI call (M11.A.3). The wrapper invokes
/// this AFTER every LLM round-trip — success or failure — so the audit
/// log captures provider outages + quota errors alongside cost data.
/// <para>
/// Implementations live outside <c>KrakenDeploy.Ai</c> so this project
/// doesn't drag EF Core / Postgres into the SDK surface. The typical
/// impl (in <c>KrakenDeploy.Server.Data</c>) writes an
/// <c>AiCallLog</c> row resolving the Space + user from the ambient
/// request context.
/// </para>
/// <para>
/// The sink MUST NOT throw — a sink failure must not break the user-facing
/// AI call. Implementations log internally and swallow.
/// </para>
/// </summary>
public interface IKrakenAiCallSink
{
    ValueTask WriteAsync(AiCallLogEntry entry, CancellationToken ct = default);
}

/// <summary>
/// Data carried from the <see cref="IKrakenAi"/> wrapper to the sink.
/// Provider-agnostic — no <c>IChatClient</c> types leak across the
/// boundary so a sink impl can be exercised in isolation.
/// </summary>
public sealed record AiCallLogEntry
{
    public required string Provider { get; init; }
    public required string Model    { get; init; }
    public required string Feature  { get; init; }

    public int PromptTokens     { get; init; }
    public int CompletionTokens { get; init; }
    public int LatencyMs        { get; init; }

    /// <summary>
    /// Computed cost in USD. Zero when the rate table doesn't know this
    /// provider/model combination — the admin UI surfaces that gap so an
    /// operator can correct the rate table.
    /// </summary>
    public decimal CostUsd { get; init; }

    public bool    Success      { get; init; }
    public string? ErrorMessage { get; init; }

    public string? CorrelationId         { get; init; }
    public string? ScrubbedVariableNames { get; init; }

    /// <summary>
    /// Serialised prompt (the <c>ChatMessage[]</c> we sent the provider).
    /// Populated only when <see cref="KrakenAiSettings.LogPromptBodies"/>
    /// was on for this Space at call time.
    /// </summary>
    public string? PromptBodyJson { get; init; }

    /// <summary>
    /// Response text body. Same opt-in gating as <see cref="PromptBodyJson"/>.
    /// </summary>
    public string? ResponseBody { get; init; }
}

/// <summary>
/// Default no-op sink registered when the host doesn't supply one. Lets
/// unit tests + minimal-bootstrap scenarios use <see cref="IKrakenAi"/>
/// without an audit-table dependency. Production builds register
/// <c>DbKrakenAiCallSink</c> instead.
/// </summary>
public sealed class NullKrakenAiCallSink : IKrakenAiCallSink
{
    public ValueTask WriteAsync(AiCallLogEntry entry, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
