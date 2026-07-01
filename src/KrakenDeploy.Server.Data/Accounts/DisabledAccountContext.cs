using KrakenDeploy.Server.Core.Domain.Accounts;

namespace KrakenDeploy.Server.Data.Accounts;

/// <summary>
/// Default <see cref="IAccountContext"/> for single-instance installs (and for CLI,
/// migrations, and tests): multi-account is OFF, so there is no tenant override and
/// the <c>DbContext</c> uses the connection configured on its options.
/// <para>
/// Registered by <c>AddKrakenDeployData</c> so the tenant <c>DbContext</c> always has
/// an <see cref="IAccountContext"/> to construct against; the Server replaces it with
/// <c>HttpAccountContext</c> when <c>MultiAccount:Enabled</c> is set. The account
/// accessors throw — nothing should read them when multi-account is off.
/// </para>
/// </summary>
public sealed class DisabledAccountContext : IAccountContext
{
    public Guid CurrentAccountId => throw NotEnabled();

    public string Subdomain => throw NotEnabled();

    public string ConnectionStringRef => throw NotEnabled();

    public string ConnectionString => throw NotEnabled();

    public bool IsResolved => false;

    // No override — the DbContext keeps the connection baked into its options.
    public string? ResolveTenantConnectionString() => null;

    public void SetResolved(ResolvedAccount account) => throw NotEnabled();

    public IDisposable WithAccount(ResolvedAccount account) => throw NotEnabled();

    private static InvalidOperationException NotEnabled() =>
        new("Multi-account mode is not enabled; there is no business-account context.");
}
