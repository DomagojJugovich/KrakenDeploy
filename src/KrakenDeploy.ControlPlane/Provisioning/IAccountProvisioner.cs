namespace KrakenDeploy.ControlPlane.Provisioning;

/// <summary>
/// Orchestrates the end-to-end provisioning saga for a new business account (§10):
/// validate → select shard → create database → register catalog row → migrate +
/// seed + first admin → mark Active. Idempotent steps; on any failure the saga
/// compensates in reverse so no orphaned database, secret, or catalog row remains.
/// </summary>
public interface IAccountProvisioner
{
    Task<ProvisioningResult> ProvisionAsync(NewAccountRequest req, CancellationToken ct = default);
}
