using KrakenDeploy.Server.Core.Domain.Deployments;

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
        _                                      => "kraken-matrix-status kraken-matrix-status--pending",
    };
}
