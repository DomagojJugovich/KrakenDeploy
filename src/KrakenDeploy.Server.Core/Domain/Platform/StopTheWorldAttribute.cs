namespace KrakenDeploy.Server.Core.Domain.Platform;

/// <summary>
/// Marks an EF migration as NON-ADDITIVE (BG1/T4): applying it while another
/// release of the server is still live would break that release (dropped/renamed
/// columns or tables, narrowing type changes, moved data). Under the blue-green
/// topologies, <c>database upgrade</c>/<c>setup</c> refuse a marked pending
/// migration while the release registry shows another non-Retired release —
/// the operator runs the stop-the-world runbook (docs/on-prem-guide.md) and
/// passes <c>--stop-the-world</c> instead. Purely-additive pending sets proceed;
/// that IS the rolling upgrade.
/// <para>
/// Put it on the migration class, beside <c>[Migration("...")]</c>. WP-BASELINE's
/// migration lint verifies markers by operation analysis (a destructive operation
/// without the marker fails CI), so the knowledge is captured when the migration
/// is written — the operator needs no foresight (T10).
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class StopTheWorldAttribute : Attribute
{
    /// <summary>Optional one-line reason shown in the refusal message.</summary>
    public string? Reason { get; init; }
}
