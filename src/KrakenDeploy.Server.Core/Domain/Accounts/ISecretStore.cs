namespace KrakenDeploy.Server.Core.Domain.Accounts;

/// <summary>
/// Stores and resolves secrets (e.g. per-account tenant DB connection strings) by
/// an opaque reference. The catalog persists only the <em>reference</em>; the raw
/// secret lives here. The default tier maps one connection string per account.
/// <para>
/// Production should back this with DPAPI / a vault. The bundled file-backed
/// implementation is intended for development and single-host installs.
/// </para>
/// </summary>
public interface ISecretStore
{
    /// <summary>Resolves a reference to its raw secret. Throws if the reference is unknown.</summary>
    Task<string> ResolveAsync(string secretRef, CancellationToken ct = default);

    /// <summary>Stores (or overwrites) the secret under the given reference; returns the reference.</summary>
    Task<string> StoreAsync(string secretRef, string secretValue, CancellationToken ct = default);

    /// <summary>Removes the secret for the given reference (no-op if absent).</summary>
    Task RemoveAsync(string secretRef, CancellationToken ct = default);
}
