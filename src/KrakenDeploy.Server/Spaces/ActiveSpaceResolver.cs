namespace KrakenDeploy.Server.Spaces;

/// <summary>
/// Pure resolution of the active <see cref="Core.Domain.Spaces.Space"/> id from
/// a candidate (the <c>kraken-active-space</c> cookie, or the circuit-carried
/// hint) against the set of Spaces the user may actually access. Centralises the
/// fail-closed fallback so the request middleware and the interactive-circuit
/// boundary resolve identically.
/// <para>
/// The candidate is never trusted on its own — it is honoured only if it is in
/// the accessible set. This is what closes the cross-tenant leak: an attacker
/// who sets the cookie (or tampers the persisted circuit hint) to a Space they
/// are not a member of gets it discarded here, not honoured.
/// </para>
/// </summary>
public static class ActiveSpaceResolver
{
    /// <summary>
    /// Resolves the active Space id.
    /// <list type="number">
    ///   <item>The <paramref name="candidate"/> if it is in <paramref name="accessible"/>.</item>
    ///   <item>Else <paramref name="defaultSpaceId"/> if the user can access it.</item>
    ///   <item>Else any accessible Space (deterministic, lowest id) so a member of
    ///         only non-Default Spaces still lands somewhere they can use.</item>
    ///   <item>Else <see cref="System.Guid.Empty"/> — a sentinel that matches no
    ///         Space, so the global query filter returns nothing. We deliberately
    ///         do NOT fall back to the Default Space: that would leak the Default
    ///         Space's data to a user who is not a member of it.</item>
    /// </list>
    /// </summary>
    public static Guid Resolve(Guid? candidate, IReadOnlySet<Guid> accessible, Guid defaultSpaceId)
    {
        ArgumentNullException.ThrowIfNull(accessible);

        if (candidate is { } c && accessible.Contains(c))
        {
            return c;
        }

        if (accessible.Contains(defaultSpaceId))
        {
            return defaultSpaceId;
        }

        if (accessible.Count > 0)
        {
            // Deterministic pick so the resolved Space is stable across requests
            // and the self-healed cookie doesn't flap.
            return accessible.Min();
        }

        // Fail closed: no accessible Space.
        return Guid.Empty;
    }
}
