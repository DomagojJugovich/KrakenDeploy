using KrakenDeploy.Server.Core.Domain.Deployments;
using Radzen;

namespace KrakenDeploy.Server.Components;

/// <summary>Small presentation helpers shared across Razor components.</summary>
public static class KrakenText
{
    /// <summary>Up to two uppercase initials for an avatar, derived from a display
    /// name or an email local-part (e.g. "domagoj.jugovic@…" → "DJ", "Acme Corp" → "AC").</summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var local = name.Contains('@') ? name[..name.IndexOf('@')] : name;
        var parts = local.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);

        var initials = parts.Length >= 2
            ? $"{parts[0][0]}{parts[1][0]}"
            : parts.Length == 1
                ? (parts[0].Length >= 2 ? parts[0][..2] : parts[0])
                : "?";

        return initials.ToUpperInvariant();
    }
}

/// <summary>
/// Maps an <see cref="InterruptionStatus"/> to its label and badge style.
/// <para>
/// Shared rather than private to a component (WP3-b) because the previous private copy
/// used a <c>_ =&gt;</c> default that silently absorbed the newly added
/// <see cref="InterruptionStatus.Cancelled"/>: a gate closed because its deployment was
/// cancelled rendered in the approval history labelled "Pending" with a neutral badge —
/// telling a reviewer the change was still awaiting a decision, which is the exact
/// misreading the Cancelled state was introduced to remove. Every arm is explicit here, so
/// a status added later throws instead of being quietly mislabelled.
/// </para>
/// </summary>
public static class InterruptionStatusVisuals
{
    public static string Label(InterruptionStatus s) => s switch
    {
        InterruptionStatus.Pending   => "Pending",
        InterruptionStatus.Approved  => "Approved",
        InterruptionStatus.Rejected  => "Rejected",
        InterruptionStatus.TimedOut  => "Timed out",
        InterruptionStatus.Cancelled => "Closed (task cancelled)",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "Unmapped gate status."),
    };

    public static BadgeStyle Badge(InterruptionStatus s) => s switch
    {
        InterruptionStatus.Pending   => BadgeStyle.Light,
        InterruptionStatus.Approved  => BadgeStyle.Success,
        InterruptionStatus.Rejected  => BadgeStyle.Danger,
        InterruptionStatus.TimedOut  => BadgeStyle.Warning,
        // Not Danger: nobody refused anything. The task went terminal underneath the gate,
        // so the question became moot — visually neutral, matching the wording.
        InterruptionStatus.Cancelled => BadgeStyle.Secondary,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "Unmapped gate status."),
    };
}

/// <summary>Maps a <see cref="DeploymentStatus"/> to the icon and CSS class used by the
/// dashboard / project deployment-status matrix cells.</summary>
public static class DeploymentStatusVisuals
{
    public static string Icon(DeploymentStatus s) => s switch
    {
        DeploymentStatus.Succeeded             => "check",
        DeploymentStatus.SucceededWithWarnings => "warning_amber",
        DeploymentStatus.Failed                => "close",
        DeploymentStatus.Cancelled             => "block",
        DeploymentStatus.Running               => "autorenew",
        DeploymentStatus.Queued                => "schedule",
        DeploymentStatus.PendingOfflineResult  => "cloud_off",
        DeploymentStatus.Paused                => "pause_circle",
        _                                      => "help",
    };

    public static string CssClass(DeploymentStatus s) => s switch
    {
        DeploymentStatus.Succeeded             => "kraken-matrix-status kraken-matrix-status--ok",
        DeploymentStatus.SucceededWithWarnings => "kraken-matrix-status kraken-matrix-status--warn",
        DeploymentStatus.Failed                => "kraken-matrix-status kraken-matrix-status--err",
        DeploymentStatus.Cancelled             => "kraken-matrix-status kraken-matrix-status--cancelled",
        DeploymentStatus.Running               => "kraken-matrix-status kraken-matrix-status--running",
        DeploymentStatus.PendingOfflineResult  => "kraken-matrix-status kraken-matrix-status--offline",
        DeploymentStatus.Paused                => "kraken-matrix-status kraken-matrix-status--warn",
        _                                      => "kraken-matrix-status kraken-matrix-status--pending",
    };
}
