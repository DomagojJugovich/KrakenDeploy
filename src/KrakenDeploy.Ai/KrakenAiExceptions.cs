namespace KrakenDeploy.Ai;

/// <summary>
/// Base type for every exception <see cref="IKrakenAi"/> may surface. Distinct
/// from raw provider exceptions so callers can catch the AI surface
/// independently of the underlying <c>IChatClient</c> backend.
/// </summary>
public abstract class KrakenAiException : Exception
{
    protected KrakenAiException(string message) : base(message) { }
    protected KrakenAiException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when a Space attempts to call <see cref="IKrakenAi"/> while its
/// configured provider is <see cref="KrakenAiProvider.Disabled"/> (or the
/// API key / model is unset). Callers should treat this as a "feature not
/// configured" condition — never as a service outage.
/// </summary>
public sealed class KrakenAiDisabledException : KrakenAiException
{
    public KrakenAiDisabledException(string reason)
        : base($"AI is not configured for this Space: {reason}") { }
}

/// <summary>
/// Thrown when the Space's monthly budget cap (<see cref="KrakenAiSettings.BudgetUsdPerMonth"/>)
/// has been reached. Callers in non-critical features (process assistant)
/// should swallow and degrade gracefully. Callers in critical features
/// (autonomous diagnosis) should log the condition; the deployment must NOT
/// fail because of a budget overrun.
/// </summary>
public sealed class KrakenAiBudgetExceededException : KrakenAiException
{
    public KrakenAiBudgetExceededException(decimal monthToDateUsd, decimal capUsd)
        : base($"AI budget exceeded: ${monthToDateUsd:F2} spent this month against a ${capUsd:F2} cap.")
    {
        MonthToDateUsd = monthToDateUsd;
        CapUsd         = capUsd;
    }

    public decimal MonthToDateUsd { get; }
    public decimal CapUsd         { get; }
}

/// <summary>
/// Thrown when this Space's feature flag (e.g.
/// <see cref="KrakenAiFeatureFlags.DiagnosisEnabled"/>) is off but a caller
/// in that feature invoked <see cref="IKrakenAi"/>. Lets the admin keep AI
/// generally enabled while turning specific features off — useful when
/// rolling out a new feature carefully or after an incident.
/// </summary>
public sealed class KrakenAiFeatureDisabledException : KrakenAiException
{
    public KrakenAiFeatureDisabledException(string feature)
        : base($"AI feature '{feature}' is disabled for this Space.")
    {
        Feature = feature;
    }

    public string Feature { get; }
}
