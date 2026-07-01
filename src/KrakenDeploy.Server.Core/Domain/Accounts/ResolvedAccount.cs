namespace KrakenDeploy.Server.Core.Domain.Accounts;

/// <summary>
/// The result of resolving a request's subdomain to an active business account.
/// Carries the routing facts the request pipeline needs — including the already
/// resolved tenant DB connection string (resolved from the secret reference by the
/// resolver, kept in-memory per scope, never persisted).
/// <para>
/// A primitive DTO living in Server.Core so the request pipeline and the
/// account-aware <c>DbContextFactory</c> can depend on it without referencing the
/// control-plane project (which owns the catalog entities).
/// </para>
/// </summary>
public sealed record ResolvedAccount(
    Guid Id,
    string Subdomain,
    string ConnectionStringRef,
    string ConnectionString);
