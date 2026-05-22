namespace KrakenDeploy.Ai;

/// <summary>
/// Per-1k-token pricing for a (<see cref="KrakenAiProvider"/>, model) pair
/// (M11.A.5). Stored on the audit row at insert time so historical cost
/// data stays accurate after provider price changes.
/// <para>
/// Sourced from public provider pricing pages, anchored at the rate-table
/// version date in <see cref="AiCostCatalog.RateTableVersionUtc"/>. Operators
/// who need accurate cost reporting against an enterprise deal that diverges
/// from list price should override <see cref="IAiCostCatalog"/> in DI.
/// </para>
/// </summary>
public sealed record AiRateCard(
    decimal InputUsdPer1k,
    decimal OutputUsdPer1k);

/// <summary>
/// Resolves the rate card for a given provider + model. Returns
/// <c>null</c> when the catalog doesn't recognise the pair — callers
/// (the wrapper) log a warning + persist zero cost rather than fail
/// the call. Better to allow + warn than to silently block a new
/// model rollout because the catalog hasn't been updated.
/// </summary>
public interface IAiCostCatalog
{
    AiRateCard? TryGetRate(KrakenAiProvider provider, string model);
}

/// <summary>
/// Default <see cref="IAiCostCatalog"/> with hardcoded list prices for the
/// well-known providers + models. Operators can override per-installation
/// by registering a custom impl before <c>AddKrakenAi()</c>.
/// </summary>
public sealed class AiCostCatalog : IAiCostCatalog
{
    /// <summary>
    /// Date the embedded rate table was last refreshed against public
    /// provider pricing pages. Operators with enterprise deals or custom
    /// negotiated rates should override <see cref="IAiCostCatalog"/> in DI;
    /// this default is sized for "good enough for billing visibility, not
    /// for invoice reconciliation."
    /// </summary>
    public static DateTimeOffset RateTableVersionUtc { get; } =
        new(2026, 5, 22, 0, 0, 0, TimeSpan.Zero);

    // Rate table — input / output USD per 1k tokens.
    // Keys are case-insensitive on the model string so callers don't have
    // to perfectly match the provider's documented casing.
    //
    // Anchored at May 2026 list pricing per the public pricing pages:
    //   - https://platform.claude.com/docs/en/about-claude/pricing
    //   - https://openai.com/api/pricing/
    //   - https://api-docs.deepseek.com/quick_start/pricing
    //
    // Local / Azure-OpenAI: Local self-hosted is $0/$0 by definition.
    // Azure OpenAI deployments use OpenAI's underlying model — same rates;
    // we don't currently track per-Azure-deployment custom rates.
    private static readonly Dictionary<RateKey, AiRateCard> _rates =
        new Dictionary<RateKey, AiRateCard>
        {
            // Anthropic (May 2026 list prices, per 1M divided by 1000)
            [new(KrakenAiProvider.Anthropic, "claude-haiku-4-5")]     = new(0.001m,  0.005m),
            [new(KrakenAiProvider.Anthropic, "claude-haiku-4.5")]     = new(0.001m,  0.005m),
            [new(KrakenAiProvider.Anthropic, "claude-sonnet-4-6")]    = new(0.003m,  0.015m),
            [new(KrakenAiProvider.Anthropic, "claude-sonnet-4.6")]    = new(0.003m,  0.015m),
            [new(KrakenAiProvider.Anthropic, "claude-opus-4-7")]      = new(0.005m,  0.025m),
            [new(KrakenAiProvider.Anthropic, "claude-opus-4.7")]      = new(0.005m,  0.025m),
            // Legacy 3.x identifiers still appear on some Anthropic-Bedrock deployments.
            [new(KrakenAiProvider.Anthropic, "claude-3-5-sonnet-20241022")] = new(0.003m,  0.015m),
            [new(KrakenAiProvider.Anthropic, "claude-3-5-haiku-20241022")]  = new(0.0008m, 0.004m),
            [new(KrakenAiProvider.Anthropic, "claude-3-opus-20240229")]     = new(0.015m,  0.075m),

            // OpenAI (May 2026 list prices)
            [new(KrakenAiProvider.OpenAI, "gpt-5.5")]        = new(0.005m,    0.030m),
            [new(KrakenAiProvider.OpenAI, "gpt-5.4")]        = new(0.0025m,   0.015m),
            [new(KrakenAiProvider.OpenAI, "gpt-5")]          = new(0.000625m, 0.005m),
            [new(KrakenAiProvider.OpenAI, "gpt-4o")]         = new(0.0025m,   0.010m),
            [new(KrakenAiProvider.OpenAI, "gpt-4o-mini")]    = new(0.00015m,  0.0006m),
            [new(KrakenAiProvider.OpenAI, "o1")]             = new(0.015m,    0.060m),
            [new(KrakenAiProvider.OpenAI, "o1-mini")]        = new(0.003m,    0.012m),

            // Azure OpenAI mirrors the underlying OpenAI model rates by
            // default. Enterprise EA pricing varies; override in DI.
            [new(KrakenAiProvider.AzureOpenAI, "gpt-5.5")]        = new(0.005m,    0.030m),
            [new(KrakenAiProvider.AzureOpenAI, "gpt-5.4")]        = new(0.0025m,   0.015m),
            [new(KrakenAiProvider.AzureOpenAI, "gpt-5")]          = new(0.000625m, 0.005m),
            [new(KrakenAiProvider.AzureOpenAI, "gpt-4o")]         = new(0.0025m,   0.010m),
            [new(KrakenAiProvider.AzureOpenAI, "gpt-4o-mini")]    = new(0.00015m,  0.0006m),

            // DeepSeek (May 2026 list — well-known low-cost models)
            [new(KrakenAiProvider.DeepSeek, "deepseek-chat")]     = new(0.00027m, 0.00110m),
            [new(KrakenAiProvider.DeepSeek, "deepseek-reasoner")] = new(0.00055m, 0.00219m),

            // Local: self-hosted on operator hardware. We can't price
            // electricity / GPU amortisation, so cost = 0. Operators who
            // want internal chargeback should override.
            // Any model name maps to the same zero card — handled via the
            // fallback in TryGetRate below, not entries here.
        };

    public AiRateCard? TryGetRate(KrakenAiProvider provider, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        // Local: zero cost by definition.
        if (provider == KrakenAiProvider.LocalOpenAiCompatible)
        {
            return new AiRateCard(0m, 0m);
        }

        return _rates.TryGetValue(new RateKey(provider, model), out var card)
            ? card
            : null;
    }

    private readonly record struct RateKey(KrakenAiProvider Provider, string Model)
    {
        public bool Equals(RateKey other) =>
            Provider == other.Provider
            && string.Equals(Model, other.Model, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(Provider, Model.ToLowerInvariant());
    }
}
