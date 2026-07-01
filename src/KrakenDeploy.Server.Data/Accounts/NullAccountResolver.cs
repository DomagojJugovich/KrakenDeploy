using KrakenDeploy.Server.Core.Domain.Accounts;

namespace KrakenDeploy.Server.Data.Accounts;

/// <summary>
/// No-op <see cref="IAccountResolver"/> for single-instance installs: never resolves
/// an account. Registered by <c>AddKrakenDeployData</c> so components/services can
/// inject <see cref="IAccountResolver"/> unconditionally; the control plane replaces
/// it with the catalog-backed resolver when multi-account is enabled.
/// </summary>
public sealed class NullAccountResolver : IAccountResolver
{
    public Task<ResolvedAccount?> ResolveAsync(string host, CancellationToken ct = default)
        => Task.FromResult<ResolvedAccount?>(null);

    public Task<ResolvedAccount?> ResolveByIdAsync(Guid accountId, CancellationToken ct = default)
        => Task.FromResult<ResolvedAccount?>(null);
}
