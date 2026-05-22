namespace KrakenDeploy.Server.Core.Domain.Licensing;

/// <summary>
/// Thrown by data-layer write paths when a create operation would exceed a
/// license quota (max targets, max users, ...). The <see cref="Exception.Message"/>
/// is the user-facing reason returned by <see cref="ILicenseGate"/> — safe to
/// surface verbatim in UI and HTTP 402-style responses.
///
/// Callers should catch this distinct from <see cref="InvalidOperationException"/>
/// so the UI can render the "Manage License" CTA next to the error, instead of
/// the generic "something went wrong" toast.
/// </summary>
public sealed class LicenseLimitException : Exception
{
    public LicenseLimitException(string message) : base(message) { }
}
