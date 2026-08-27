namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// Evicts cached per-account OIDC scheme + options state when an
/// <see cref="IdentityProvider"/> is created, updated, or deleted, so the change
/// takes effect without a process restart.
/// <para>
/// In SaaS multi-account mode external OIDC schemes are synthesized per request from
/// the active account's database, and their <c>OpenIdConnectOptions</c> are cached
/// process-wide by <c>IOptionsMonitor</c> — a farm restart per tenant edit is not
/// acceptable, so an edit must evict the stale entry. The no-op default (registered
/// by <c>AddKrakenDeployData</c>) preserves single-instance behaviour, where the
/// static startup registration already implies "restart to apply". The Server
/// replaces it with a real evictor under <c>Deployment:Topology=Saas</c>.
/// </para>
/// </summary>
public interface IOidcSchemeCacheInvalidator
{
    /// <summary>
    /// Invalidates any cached scheme/options for the given provider in the
    /// <b>current</b> account's context (the active account is resolved from the
    /// request — IdP administration is always a per-tenant operation).
    /// </summary>
    void Invalidate(Guid providerId);
}
