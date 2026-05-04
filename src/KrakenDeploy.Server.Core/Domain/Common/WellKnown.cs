namespace KrakenDeploy.Server.Core.Domain.Common;

/// <summary>
/// Well-known fixed identifiers seeded into the database. Used so that domain
/// code can reference the bootstrap rows (Default Space, Default Project Group,
/// etc.) by a stable Guid without a database lookup.
/// </summary>
public static class WellKnown
{
    /// <summary>
    /// The single Space that is auto-created on first run. On-prem installs
    /// typically use this Space and never create another (the UI hides the
    /// Space switcher when only the Default Space exists). Cloud SaaS uses
    /// this Space only for platform-level admin and creates one Space per
    /// customer alongside it.
    /// </summary>
    public static readonly Guid DefaultSpaceId =
        new("00000000-0000-0000-0000-00000000d543"); // "default" leetspeak-ish

    /// <summary>Slug used for the Default Space.</summary>
    public const string DefaultSpaceSlug = "default";

    /// <summary>Display name used for the Default Space.</summary>
    public const string DefaultSpaceName = "Default";
}
