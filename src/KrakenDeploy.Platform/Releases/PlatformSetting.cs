namespace KrakenDeploy.Platform.Releases;

/// <summary>
/// Typed key/value row for platform-global settings (one row per key). First
/// consumer: <see cref="PlatformSettingKeys.CurrentDefaultRelease"/> — the single
/// pointer for "where new sessions/agents go"
/// (docs/blue-green-slot-deployment.md §4).
/// </summary>
public class PlatformSetting
{
    public required string Key { get; set; }

    public required string Value { get; set; }

    public DateTimeOffset ModifiedUtc { get; set; }
}

/// <summary>Well-known <see cref="PlatformSetting"/> keys.</summary>
public static class PlatformSettingKeys
{
    /// <summary>Release id new sessions/agents are routed to (blue-green default pointer).</summary>
    public const string CurrentDefaultRelease = "current_default_release";
}
