using KrakenDeploy.Server.Core.Domain.Licensing;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Lightweight <see cref="ILicenseGate"/> fake for unit / integration tests.
/// Default factory <see cref="Unlimited"/> always allows. Use the constructor
/// to inject a refusal message for negative-path tests.
/// </summary>
internal sealed class FakeLicenseGate(
    string? targetRefusal = null,
    string? userRefusal = null) : ILicenseGate
{
    /// <summary>
    /// Gate that never refuses — pass-through for the dozens of integration
    /// tests that exercise unrelated code but happen to instantiate
    /// <c>TargetRegistrationService</c> / <c>UserService</c>.
    /// </summary>
    public static readonly ILicenseGate Unlimited = new FakeLicenseGate();

    public string? CheckTargetCreate(int currentTargetCount) => targetRefusal;
    public string? CheckUserCreate(int currentUserCount) => userRefusal;
}
