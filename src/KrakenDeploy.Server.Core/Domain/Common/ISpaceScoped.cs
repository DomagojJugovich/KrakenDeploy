namespace KrakenDeploy.Server.Core.Domain.Common;

/// <summary>
/// Marker interface for entities that belong to a single <see cref="Spaces.Space"/>.
/// <para>
/// Used by:
/// <list type="bullet">
///   <item><c>SpaceScopingInterceptor</c> — auto-stamps <see cref="SpaceId"/> on insert
///         from the current <c>ISpaceContext</c> when the caller hasn't set it.</item>
///   <item><c>KrakenDbContext.OnModelCreating</c> — applies an EF Core global query
///         filter so every read auto-restricts to the current Space.</item>
///   <item>Migration generator — knows which tables need the <c>space_id</c> column
///         and FK to <c>spaces.id</c>.</item>
/// </list>
/// </para>
/// <para>
/// Both aggregate roots and Space-scoped child entities (e.g. <c>Variable</c>,
/// <c>ProcessStep</c>, <c>TaskArtifact</c>) implement this so the query filter and
/// insert-stamping apply uniformly. They differ in how <c>space_id</c> integrity is
/// enforced at the DB level: aggregate roots carry a direct FK to <c>spaces</c>, while
/// children carry a <em>composite</em> FK <c>(space_id, parent_id) → parent(space_id, id)</c>
/// that transitively pins them to their parent's Space (so a child can never reference a
/// parent in another Space). A few high-volume or purely-transitive tables
/// (e.g. <c>task_step_logs</c>, <c>task_log_live</c>) deliberately do <em>not</em>
/// implement this and reach a Space only through their single owning FK.
/// </para>
/// </summary>
public interface ISpaceScoped
{
    /// <summary>FK to the owning Space. Auto-stamped on insert when zero.</summary>
    Guid SpaceId { get; set; }
}
