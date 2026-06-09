using KrakenDeploy.Server.Core.Domain.Variables;

namespace KrakenDeploy.Server.Core.Domain.Releases;

/// <summary>
/// A single frozen variable that travelled with a <see cref="Release"/> at
/// release-creation (or last "Update Variables") time. The release's
/// <see cref="Release.VariableSnapshot"/> is a list of these.
/// <para>
/// Same shape as the live <see cref="Variable"/> entity in everything that
/// affects deployment resolution — name, value, type, scope — but without
/// the EF identity (no <c>Id</c>, no <c>SetId</c>, no audit timestamps).
/// Stored verbatim inside <c>releases.variable_snapshot</c> as JSONB so
/// the existing scope-resolver can be pointed at it identically.
/// </para>
/// <para>
/// Sensitive values stay encrypted in the snapshot — <see cref="Value"/>
/// holds the same AES-256-GCM ciphertext that lives in the source
/// <see cref="Variable"/>. Decryption still happens at deployment-resolve
/// time. The snapshot survives a project's later variable edits; it does
/// NOT automatically survive an encryption-key rotation (the key used to
/// encrypt the ciphertext must remain available).
/// </para>
/// </summary>
public sealed class VariableSnapshot
{
    /// <summary>
    /// Origin rank used by every project variable in a snapshot. Far above any
    /// library-set inclusion order so a project variable wins an <i>equal-scope</i>
    /// tie against an included library set — the resolver's tie-breaker, applied
    /// only after scope specificity.
    /// </summary>
    public const int ProjectLayer = 1_000_000;

    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public VariableType Type { get; init; }
    public VariableScope Scope { get; init; } = new();

    /// <summary>
    /// Origin rank of this snapshot entry — the resolver's tie-breaker used
    /// ONLY when two same-named entries are scoped with <i>equal</i> specificity
    /// (project beats library). Scope specificity is always the primary key.
    /// Project variables use <see cref="ProjectLayer"/>; included library-set
    /// variables use their inclusion <c>SortOrder</c>. Pre-library snapshots
    /// default to <c>0</c>, which is harmless: within a single source the
    /// tie-break never fires, so specificity alone decides.
    /// </summary>
    public int Layer { get; init; }
}
