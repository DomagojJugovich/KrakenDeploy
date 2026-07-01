namespace KrakenDeploy.ControlPlane.Provisioning;

/// <summary>
/// Outcome of a provisioning run. On failure the saga has already compensated
/// (no orphaned database / secret / catalog row), and <see cref="Error"/> carries
/// the reason.
/// </summary>
public sealed record ProvisioningResult(bool Success, Guid? AccountId, string? Subdomain, string? Error)
{
    public static ProvisioningResult Ok(Guid accountId, string subdomain) =>
        new(true, accountId, subdomain, null);

    public static ProvisioningResult Fail(string error) =>
        new(false, null, null, error);
}
