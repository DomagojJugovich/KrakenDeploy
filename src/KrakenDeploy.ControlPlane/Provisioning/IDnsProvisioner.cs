namespace KrakenDeploy.ControlPlane.Provisioning;

/// <summary>
/// Creates / removes the DNS record for an account subdomain. Under the recommended
/// wildcard DNS + wildcard TLS strategy (§11) this is a no-op — every
/// <c>*.basedomain</c> already resolves to the app tier. A real provider is only
/// needed for dedicated-infra or custom-domain accounts.
/// </summary>
public interface IDnsProvisioner
{
    Task ConfigureAsync(string subdomain, CancellationToken ct = default);

    Task RemoveAsync(string subdomain, CancellationToken ct = default);
}

/// <summary>No-op provisioner for the wildcard-DNS default (§11).</summary>
public sealed class NoopDnsProvisioner : IDnsProvisioner
{
    public Task ConfigureAsync(string subdomain, CancellationToken ct = default) => Task.CompletedTask;

    public Task RemoveAsync(string subdomain, CancellationToken ct = default) => Task.CompletedTask;
}
