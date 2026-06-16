using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Spaces;

/// <summary>
/// Picks a Space the caller can actually reach, for <see cref="SpaceScopedComponentBase"/>
/// to fall back to when the URL names an unknown / inaccessible Space.
/// </summary>
public static class SpaceResolution
{
    /// <summary>
    /// The slug to bounce an inaccessible URL to: the Default Space if accessible,
    /// else any accessible Active Space (lowest name), else <c>null</c> (the user
    /// can reach no Space at all).
    /// </summary>
    public static async Task<string?> ResolveAccessibleSlugAsync(
        SpaceService spaces,
        IReadOnlySet<Guid> accessible,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spaces);
        ArgumentNullException.ThrowIfNull(accessible);

        if (accessible.Count == 0)
        {
            return null;
        }

        var all = await spaces.GetAllAsync(ct).ConfigureAwait(false);
        var usable = all
            .Where(s => s.Status == SpaceStatus.Active && accessible.Contains(s.Id))
            .ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        return (usable.FirstOrDefault(s => s.IsDefault)
                ?? usable.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).First())
            .Slug;
    }
}
