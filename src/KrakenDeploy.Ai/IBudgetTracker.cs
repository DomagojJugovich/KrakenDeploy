namespace KrakenDeploy.Ai;

/// <summary>
/// Provides the current Space's month-to-date AI spend in USD (M11.A.5).
/// The wrapper checks this BEFORE every call and refuses with
/// <see cref="KrakenAiBudgetExceededException"/> when MTD ≥
/// <see cref="KrakenAiSettings.BudgetUsdPerMonth"/>.
/// <para>
/// Why pre-check (instead of estimating the cost of THIS call and adding
/// it)? Token-count-based pre-estimation is unreliable — prompts vary,
/// completion lengths vary even more. We accept a small overshoot risk
/// (one call after the cap is reached can push MTD slightly over) in
/// exchange for predictable behaviour: once MTD hits the cap, no more
/// calls go out.
/// </para>
/// </summary>
public interface IBudgetTracker
{
    /// <summary>
    /// Returns total USD spent in the current UTC calendar month for the
    /// ambient Space. Background-job paths that haven't established a
    /// Space context return 0 — the caller's call still goes out, but
    /// nothing's attributable to a Space's budget anyway.
    /// </summary>
    ValueTask<decimal> GetMonthToDateUsdAsync(CancellationToken ct = default);
}

/// <summary>
/// Default no-op tracker registered when the host hasn't wired the
/// EF-backed one. Returns zero, so budget enforcement is effectively
/// disabled in tests + minimal-bootstrap scenarios.
/// </summary>
public sealed class NullBudgetTracker : IBudgetTracker
{
    public ValueTask<decimal> GetMonthToDateUsdAsync(CancellationToken ct = default)
        => ValueTask.FromResult(0m);
}
