namespace KrakenDeploy.Server.Core.Domain.Accounts;

/// <summary>
/// Per-request / per-circuit context that resolves the active business account
/// (the cross-customer isolation boundary). Mirrors <c>ISpaceContext</c> one level
/// up: where Space rides in the URL path, the account rides in the host subdomain.
/// <para>
/// Unlike <c>ISpaceContext</c> there is no default-account fallback — accessing the
/// account before it is resolved <b>throws</b> (fail closed). The active account is
/// resolved from the request subdomain by <c>AccountResolutionMiddleware</c>; the
/// resolved tenant connection string is then used by the account-aware
/// <c>IDbContextFactory&lt;KrakenDbContext&gt;</c>.
/// </para>
/// </summary>
public interface IAccountContext
{
    /// <summary>Active account id. Throws if no account has been resolved (fail closed).</summary>
    Guid CurrentAccountId { get; }

    /// <summary>Active account subdomain. Throws if unresolved.</summary>
    string Subdomain { get; }

    /// <summary>Secret-store reference for the tenant DB connection string. Throws if unresolved.</summary>
    string ConnectionStringRef { get; }

    /// <summary>
    /// The resolved tenant DB connection string for the active account. Throws if
    /// unresolved. In-memory only (resolved per scope from <see cref="ConnectionStringRef"/>),
    /// never persisted to the catalog.
    /// </summary>
    string ConnectionString { get; }

    /// <summary>True once an account has been resolved (or overridden) for this scope.</summary>
    bool IsResolved { get; }

    /// <summary>
    /// The tenant DB connection string this scope's <c>DbContext</c> should bind to,
    /// or <c>null</c> to keep the connection already configured on the context's
    /// options (single-instance mode). Multi-account implementations return the
    /// resolved account's connection and <b>throw</b> if no account is resolved
    /// (cross-customer boundary — fail closed). Called from
    /// <c>KrakenDbContext.OnConfiguring</c>.
    /// </summary>
    string? ResolveTenantConnectionString();

    /// <summary>
    /// Sets the active account for the current scope. Called by
    /// <c>AccountResolutionMiddleware</c> after resolving the request subdomain.
    /// </summary>
    void SetResolved(ResolvedAccount account);

    /// <summary>
    /// Pushes a temporary account override for the duration of the returned scope.
    /// Used by background workers / control-plane operations that act on a specific
    /// account regardless of any request-resolved account.
    /// </summary>
    IDisposable WithAccount(ResolvedAccount account);
}
