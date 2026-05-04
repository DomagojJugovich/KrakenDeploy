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
/// Child entities (e.g. <c>DeploymentStep</c>, <c>Variable</c>, <c>RunbookRunLogEntry</c>)
/// do <em>not</em> implement this — they reach a Space transitively via their parent
/// FK. Only top-level aggregates carry the direct Space FK so the query filter is fast
/// and the foreign key graph stays acyclic.
/// </para>
/// </summary>
public interface ISpaceScoped
{
    /// <summary>FK to the owning Space. Auto-stamped on insert when zero.</summary>
    Guid SpaceId { get; set; }
}
