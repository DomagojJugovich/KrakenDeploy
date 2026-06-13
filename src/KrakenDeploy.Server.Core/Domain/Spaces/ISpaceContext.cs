namespace KrakenDeploy.Server.Core.Domain.Spaces;

/// <summary>
/// Per-request context that resolves the active <see cref="Space"/> for the current
/// operation. Drives:
/// <list type="bullet">
///   <item><c>SpaceScopingInterceptor</c> — auto-stamps <c>SpaceId</c> on inserts.</item>
///   <item>EF Core global query filter — restricts reads to the current Space.</item>
///   <item>Authorization — permission scope evaluation needs to know which Space
///         the operation targets.</item>
/// </list>
/// <para>
/// Resolution order (in <c>HttpSpaceContext</c>):
/// <list type="number">
///   <item>Explicit override via <see cref="WithSpace"/> — used by background workers,
///         tests, and admin operations that need to act outside the request user's
///         active Space.</item>
///   <item><c>HttpContext.Items["space_id"]</c> — set by routing middleware for
///         slug-based URLs like <c>/s/{spaceSlug}/...</c>.</item>
///   <item>The <c>active_space_id</c> claim on the current user — set when the user
///         picks a Space from the switcher; persisted across requests via the auth
///         cookie.</item>
///   <item><see cref="Common.WellKnown.DefaultSpaceId"/> — fallback for on-prem
///         single-Space installs and unauthenticated/CLI scenarios.</item>
/// </list>
/// </para>
/// </summary>
public interface ISpaceContext
{
    /// <summary>The Space the current operation is acting in. Never empty.</summary>
    Guid CurrentSpaceId { get; }

    /// <summary>
    /// Pushes a temporary Space override for the duration of the returned scope.
    /// Used by Hangfire workers, tests, and administrative operations that act on
    /// a specific Space regardless of the request's resolved active Space.
    /// </summary>
    /// <example>
    /// <code>
    /// using (spaceContext.WithSpace(spaceId))
    /// {
    ///     await deploymentService.CreateAsync(...); // uses spaceId
    /// }
    /// </code>
    /// </example>
    IDisposable WithSpace(Guid spaceId);
}
