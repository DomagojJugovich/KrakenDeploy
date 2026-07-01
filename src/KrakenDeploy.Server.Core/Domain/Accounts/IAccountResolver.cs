namespace KrakenDeploy.Server.Core.Domain.Accounts;

/// <summary>
/// Resolves a request host to an <b>active</b> business account (subdomain → account
/// → tenant connection string). Returns <c>null</c> when the host carries no tenant
/// subdomain (apex / control-plane host) or the subdomain does not map to an active
/// account — the caller (middleware) then either passes through (control-plane) or
/// fails closed (unknown tenant subdomain). Implementations cache aggressively.
/// </summary>
public interface IAccountResolver
{
    Task<ResolvedAccount?> ResolveAsync(string host, CancellationToken ct = default);

    /// <summary>
    /// Resolves an <b>active</b> account by its id (subdomain not involved). Used by
    /// background workers that dequeue account-tagged work items and must bind to the
    /// owning tenant database. Returns <c>null</c> if the account is unknown or not active.
    /// </summary>
    Task<ResolvedAccount?> ResolveByIdAsync(Guid accountId, CancellationToken ct = default);
}
