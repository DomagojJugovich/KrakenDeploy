namespace KrakenDeploy.ControlPlane.Provisioning;

/// <summary>
/// Inputs captured at signup to provision a new business account (§10). The
/// admin credentials seed the account's first user; everything else is routing
/// metadata. Region / consent / tier selection are deferred to later phases.
/// </summary>
public sealed record NewAccountRequest(
    string Subdomain,
    string DisplayName,
    string AdminEmail,
    string AdminPassword);
