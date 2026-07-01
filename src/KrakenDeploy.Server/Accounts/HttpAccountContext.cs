using KrakenDeploy.Server.Core.Domain.Accounts;

namespace KrakenDeploy.Server.Accounts;

/// <summary>
/// HTTP / circuit-aware <see cref="IAccountContext"/>. The active account is resolved
/// once per request by <see cref="AccountResolutionMiddleware"/> and stashed in
/// <c>HttpContext.Items</c>; this reads it back via <see cref="IHttpContextAccessor"/>.
/// <para>
/// Why <c>HttpContext.Items</c> rather than a field set by the middleware: Blazor
/// renders components/services across <b>multiple DI scopes within a single request</b>
/// (the middleware scope, and per-component render scopes), so a value set on one
/// scoped instance is invisible to the others. <c>HttpContext.Items</c> is shared by
/// the whole request regardless of DI scope, so every <see cref="HttpAccountContext"/>
/// instance reads the same account.
/// </para>
/// <para>
/// Resolution order (first match wins): an explicit override pushed via
/// <see cref="WithAccount"/> (background / control-plane operations), then a value set
/// via <see cref="SetResolved"/> on this instance (the interactive circuit, which has
/// no <c>HttpContext</c> — see <c>AccountBoundary</c>), then <c>HttpContext.Items</c>
/// (every request, including SSR). Reading the account before it is resolved throws
/// (cross-customer boundary — fail closed).
/// </para>
/// </summary>
public sealed class HttpAccountContext(IHttpContextAccessor httpContextAccessor) : IAccountContext
{
    /// <summary>Key under which <see cref="AccountResolutionMiddleware"/> stashes the resolved account.</summary>
    public const string ItemsKey = "kd.account.resolved";

    // Ambient override for background / control-plane work (provisioning, fleet
    // migration, per-account recurring jobs). AsyncLocal flows across awaits AND
    // across child DI scopes the work opens, so a job that creates its own scope
    // still sees the account — a scope-local field would not. Set via WithAccount.
    private static readonly AsyncLocal<ResolvedAccount?> AmbientOverride = new();

    private ResolvedAccount? _resolved;

    private ResolvedAccount? Current =>
        AmbientOverride.Value
        ?? _resolved
        ?? httpContextAccessor.HttpContext?.Items[ItemsKey] as ResolvedAccount;

    public Guid CurrentAccountId => (Current ?? throw NotResolved()).Id;

    public string Subdomain => (Current ?? throw NotResolved()).Subdomain;

    public string ConnectionStringRef => (Current ?? throw NotResolved()).ConnectionStringRef;

    public string ConnectionString => (Current ?? throw NotResolved()).ConnectionString;

    public bool IsResolved => Current is not null;

    // Multi-account always overrides the connection (or throws if unresolved — a
    // KrakenDbContext must never be built without a resolved account).
    public string? ResolveTenantConnectionString() => (Current ?? throw NotResolved()).ConnectionString;

    /// <summary>
    /// Pins the account for the interactive circuit (which has no <c>HttpContext</c>).
    /// Set by <c>AccountBoundary</c> from the connection host. SSR/request scopes read
    /// the account from <c>HttpContext.Items</c> instead.
    /// </summary>
    public void SetResolved(ResolvedAccount account) => _resolved = account;

    public IDisposable WithAccount(ResolvedAccount account)
    {
        var previous = AmbientOverride.Value;
        AmbientOverride.Value = account;
        return new RestoreOnDispose(previous);
    }

    private static InvalidOperationException NotResolved() =>
        new("No business account has been resolved for this scope. The account is resolved " +
            "from the request subdomain by AccountResolutionMiddleware; tenant data must never " +
            "be accessed without a resolved account (cross-customer boundary — fail closed).");

    private sealed class RestoreOnDispose(ResolvedAccount? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            AmbientOverride.Value = previous;
        }
    }
}
