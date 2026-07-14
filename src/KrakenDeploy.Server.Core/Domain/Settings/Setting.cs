using KrakenDeploy.Server.Core.Domain.Common;

namespace KrakenDeploy.Server.Core.Domain.Settings;

/// <summary>
/// One row of the unified <c>settings</c> table: a JSON <see cref="Payload"/>
/// document addressed by (<see cref="ScopeType"/>, <see cref="ScopeId"/>,
/// <see cref="Key"/>). Replaces the former <c>smtp_settings</c>,
/// <c>backup_settings</c>, <c>maintenance_settings</c>,
/// <c>performance_settings</c>, <c>feature_flags</c>, and
/// <c>space_ai_settings</c> tables.
///
/// <para>
/// <strong>Not <c>ISpaceScoped</c>.</strong> The scope discriminator is nullable
/// (System documents have no Space), so this table gets no global query filter.
/// All scoping — resolving the right (scope_type, scope_id) for a key, and
/// caging Space documents to the caller's Space — lives exclusively in
/// <see cref="SettingsService"/>. An architecture test asserts no code outside
/// that service touches the <see cref="Setting"/> DbSet.
/// </para>
/// <para>
/// Derives from <see cref="AuditableEntity"/> so the
/// <c>AuditLogInterceptor</c> records a <c>Setting.Created/Updated</c> audit
/// entry on every save (with <c>SubjectName = key</c> so entries stay
/// distinguishable across the folded document types). Carries a PostgreSQL
/// <c>xmin</c> optimistic-concurrency token so the read-modify-write on a
/// multi-key document (the feature-flags overrides map) is race-safe.
/// </para>
/// </summary>
public class Setting : AuditableEntity
{
    /// <summary>Scope discriminator (0 = System, 1 = Space, 2 = User).</summary>
    public SettingsScope ScopeType { get; set; }

    /// <summary>The Space id for Space-scoped documents; <c>null</c> for System.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary>Document key within the scope (e.g. <c>"smtp"</c>, <c>"ai"</c>).</summary>
    public string Key { get; set; } = "";

    /// <summary>Serialized <see cref="ISettingsDocument"/> payload (jsonb column).</summary>
    public string Payload { get; set; } = "{}";
}
