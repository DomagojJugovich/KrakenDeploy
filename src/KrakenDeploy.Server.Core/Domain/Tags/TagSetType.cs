namespace KrakenDeploy.Server.Core.Domain.Tags;

/// <summary>
/// Selection cardinality of a <see cref="TagSet"/> (Octopus extended tag sets).
/// Values are stable storage contracts — append only, never renumber.
/// </summary>
public enum TagSetType
{
    /// <summary>Any number of predefined tags per entity (the default).</summary>
    MultiSelect  = 0,

    /// <summary>At most one predefined tag from the set per entity
    /// (e.g. cloud provider, deployment tier).</summary>
    SingleSelect = 1,

    /// <summary>One arbitrary text value per entity instead of predefined tags
    /// (e.g. region identifier, customer id). The set has no tag list.</summary>
    FreeText     = 2,
}
