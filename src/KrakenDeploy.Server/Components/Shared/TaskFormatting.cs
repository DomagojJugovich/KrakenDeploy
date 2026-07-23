// Namespace intentionally the parent (not .Shared): CA1716 rejects the reserved
// keyword 'Shared' as a namespace name, so the sibling helpers (KrakenGrid.cs,
// PivotLayout.cs) all live in KrakenDeploy.Server.Components. _Imports.razor
// imports it, so `TaskFormatting` still resolves unqualified in Razor.
namespace KrakenDeploy.Server.Components;

/// <summary>
/// Shared human-readable formatting for task/step durations. Consolidates the
/// compact <c>Xh Ym Zs</c> family that had drifted across the deployment/runbook
/// detail pages, the deployments list, and the step-outcomes grid — some handled
/// a sub-second (<c>&lt;1s</c>) and a bare-seconds tier, some rendered a stray
/// <c>0m</c> prefix for short spans. The deliberately-distinct presentations
/// elsewhere (the verbose word form on the Tasks list, the terse <c>Ns</c> on
/// release detail, and the <c>X.X s</c> precision on the backup section) are NOT
/// routed here — they are different by design.
/// </summary>
public static class TaskFormatting
{
    /// <summary>
    /// A duration as a compact string: <c>&lt;1s</c>, <c>Zs</c>, <c>Ym Zs</c>, or
    /// <c>Xh Ym Zs</c>. Non-positive spans (e.g. an unfinished step whose
    /// completion timestamp is still default) render as <c>&lt;1s</c>.
    /// </summary>
    public static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 1) { return "<1s"; }
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s";
        }
        if (span.TotalMinutes >= 1)
        {
            return $"{span.Minutes}m {span.Seconds}s";
        }
        return $"{span.Seconds}s";
    }

    /// <summary>
    /// Elapsed time between <paramref name="started"/> and
    /// <paramref name="completed"/> — or up to now, when still running — formatted
    /// via <see cref="Duration"/>. Returns <c>—</c> when not started.
    /// </summary>
    public static string Elapsed(DateTimeOffset? started, DateTimeOffset? completed)
        => started is { } start
            ? Duration((completed ?? DateTimeOffset.UtcNow) - start)
            : "—";
}
