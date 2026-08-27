using KrakenDeploy.Server.Core.Domain.Security;

namespace KrakenDeploy.Server.Data.Identity;

/// <summary>
/// No-op <see cref="IOidcSchemeCacheInvalidator"/> for single-instance installs (and
/// any path with no dynamic per-account OIDC). Registered by <c>AddKrakenDeployData</c>
/// so <c>IdentityProviderService</c> can depend on the invalidator unconditionally; the
/// Server replaces it with a real evictor under <c>Deployment:Topology=Saas</c>.
/// </summary>
public sealed class NullOidcSchemeCacheInvalidator : IOidcSchemeCacheInvalidator
{
    public void Invalidate(Guid providerId)
    {
        // Single-instance OIDC schemes are registered once at startup; an edit applies
        // on the next restart, so there is nothing to evict here.
    }
}
