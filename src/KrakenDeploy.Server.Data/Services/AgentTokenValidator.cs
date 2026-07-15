using System.Globalization;
using System.Security.Claims;
using KrakenDeploy.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// A8/T1-12 — the agent-token revocation check. Runs after the JWT signature,
/// lifetime, issuer and audience have already been validated by the JwtBearer
/// handler: it confirms the token's <c>atv</c> (<see cref="AgentTokenClaims.TokenVersion"/>)
/// claim still matches the target row's current <see cref="Core.Domain.Targets.DeploymentTarget.AgentTokenVersion"/>.
/// <para>
/// Fail-closed: a missing/garbled claim, a target that no longer exists, or a
/// version mismatch all reject the token. Lives in the data layer (not the app
/// project) so the production auth pipeline AND the tests exercise the exact
/// same comparison against a real database.
/// </para>
/// </summary>
public static class AgentTokenValidator
{
    public enum Outcome
    {
        /// <summary>Claim present and equal to the target's current version.</summary>
        Valid,

        /// <summary>The token lacks a usable subject or <c>atv</c> claim.</summary>
        MissingClaims,

        /// <summary>No target row for the token's subject (deleted, or — in
        /// multi-account — resolved against the wrong tenant database).</summary>
        TargetNotFound,

        /// <summary>The token's version is stale — it has been revoked.</summary>
        VersionMismatch,
    }

    /// <summary>
    /// Validates the agent principal's token version against the database.
    /// The subject is read from <see cref="ClaimTypes.NameIdentifier"/> (the JWT
    /// <c>sub</c>), the version from <see cref="AgentTokenClaims.TokenVersion"/>.
    /// The target is looked up filter-free (the hub has no ambient Space).
    /// </summary>
    public static async Task<Outcome> ValidateAsync(
        ClaimsPrincipal? principal,
        IDbContextFactory<KrakenDbContext> dbFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);

        var sub = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var atv = principal?.FindFirst(AgentTokenClaims.TokenVersion)?.Value;

        if (!Guid.TryParse(sub, out var targetId) ||
            !int.TryParse(atv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var claimVersion))
        {
            return Outcome.MissingClaims;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Filter-free: the agent's own target may live in a non-Default Space and
        // there is no ambient Space on the auth path. The target id is the
        // authenticated subject, so this is not a scope-widening read.
        var currentVersion = await db.DeploymentTargets
            .IgnoreQueryFilters()
            .Where(t => t.Id == targetId)
            .Select(t => (int?)t.AgentTokenVersion)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (currentVersion is null)
        {
            return Outcome.TargetNotFound;
        }

        return currentVersion.Value == claimVersion
            ? Outcome.Valid
            : Outcome.VersionMismatch;
    }
}
