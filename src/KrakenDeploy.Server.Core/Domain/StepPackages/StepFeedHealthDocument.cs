using KrakenDeploy.Server.Core.Domain.Settings;

namespace KrakenDeploy.Server.Core.Domain.StepPackages;

/// <summary>
/// System-scoped <see cref="ISettingsDocument"/> (key <c>"step-feeds"</c>)
/// recording the health of every step catalog feed (SC6): last attempt,
/// last success, and the last error message — so a feed that has been
/// failing silently for a week is visible in the picker's feed-health strip
/// and on the catalog pages instead of only in a LogWarning nobody reads.
/// <para>
/// Keys are <c>templates:{owner}/{repo}</c> and <c>packages:{owner}/{repo}</c>,
/// lower-cased. No secrets — the DEK-rotation walk skips it (no
/// <c>*Encrypted</c> members).
/// </para>
/// </summary>
public class StepFeedHealthDocument : ISettingsDocument
{
    public static string Key => "step-feeds";
    public static SettingsScope Scope => SettingsScope.System;

    public Dictionary<string, StepFeedHealth> Feeds { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One feed's health record.</summary>
public class StepFeedHealth
{
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }

    /// <summary>The most recent refresh failure; <c>null</c> after a clean refresh.</summary>
    public string? LastError { get; set; }
}
