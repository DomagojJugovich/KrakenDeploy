namespace KrakenDeploy.Server.Data;

/// <summary>
/// A unit of background work (a deployment, runbook run, or diagnosis) tagged with
/// the business account it belongs to, carried over the in-process dispatch channels.
/// <para>
/// The work item lives in a tenant database, so the worker can't open the right
/// database without first knowing the account — the enqueuing code (which runs in a
/// resolved-account scope) stamps <see cref="AccountId"/>, and the worker resolves it
/// and runs the work under <c>WithAccount</c>. <see cref="AccountId"/> is
/// <see cref="System.Guid.Empty"/> for single-instance installs (no account layer),
/// in which case the worker uses the fixed connection.
/// </para>
/// </summary>
public readonly record struct TenantWorkItem(Guid AccountId, Guid Id);
