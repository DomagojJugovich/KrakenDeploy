namespace KrakenDeploy.Server.Core.Domain.Settings;

/// <summary>
/// Scope a <see cref="ISettingsDocument"/> lives at. Persisted as the
/// <c>settings.scope_type</c> smallint discriminator.
/// </summary>
public enum SettingsScope
{
    /// <summary>Instance-wide — one document per key, <c>scope_id = NULL</c>.</summary>
    System = 0,

    /// <summary>Per-Space — one document per (Space, key), <c>scope_id = SpaceId</c>.</summary>
    Space = 1,

    /// <summary>Reserved for a future per-user scope. Not used yet.</summary>
    User = 2,
}

/// <summary>
/// Marker for a strongly-typed settings payload stored as the <c>jsonb</c>
/// document in a single <c>settings</c> row (fix 7 of the 2026-07-10 schema
/// hardening — six single-purpose settings tables folded into one).
///
/// <para>
/// Implementers are plain POCOs in <c>Server.Core</c>. Their public property
/// initializers ARE the backfill: <see cref="Settings.SettingsService"/> returns
/// <c>new T()</c> when no row exists, so a Space that never configured AI or an
/// operator who never visited a page transparently gets defaults with no row.
/// </para>
/// <para>
/// <strong>Secrets</strong> stay ciphertext strings in members whose name ends
/// with <c>Encrypted</c> (encrypted by the calling service via
/// <c>IEncryptionService</c> exactly as before the fold). This naming is
/// load-bearing: the DEK-rotation walk re-encrypts every <c>*Encrypted</c>
/// member of every registered document generically, and a completeness test
/// reflects over the <see cref="ISettingsDocument"/> implementors to prove no
/// secret-bearing document is missed — without it, a DEK rotation would
/// silently brick the SMTP password / AI API key.
/// </para>
/// <para>
/// The document is serialized with a <c>JsonStringEnumConverter</c> so enum
/// members round-trip as stable names, not ordinals.
/// </para>
/// </summary>
public interface ISettingsDocument
{
    /// <summary>
    /// Stable lookup key for this document within its scope — the
    /// <c>settings.key</c> column (e.g. <c>"smtp"</c>, <c>"ai"</c>). Static so
    /// the accessor and the migration address a document by type without an
    /// instance.
    /// </summary>
    static abstract string Key { get; }

    /// <summary>The scope this document lives at.</summary>
    static abstract SettingsScope Scope { get; }
}
