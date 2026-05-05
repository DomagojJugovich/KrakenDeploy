using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Security;

namespace KrakenDeploy.Server.Core.Tests;

/// <summary>
/// Lightweight tests that lock in the policy-name convention used by both
/// <c>PermissionPolicyProvider</c> (server, parses policy name → Permission)
/// and any client / Blazor code that synthesizes policy names. Since the
/// provider lives in the Server project we can't reference it directly from
/// Server.Core.Tests; we test the contract by replicating the format string.
/// </summary>
public sealed class PermissionPolicyNamingTests
{
    private const string PolicyPrefix = "perm:";

    [Theory]
    [InlineData(Permission.AdministerSystem, "perm:AdministerSystem")]
    [InlineData(Permission.ProjectView,      "perm:ProjectView")]
    [InlineData(Permission.DeploymentCreate, "perm:DeploymentCreate")]
    [InlineData(Permission.OfflineResultUpload, "perm:OfflineResultUpload")]
    public void Policy_name_format_is_perm_prefix_plus_enum_name(
        Permission permission, string expected)
    {
        var actual = PolicyPrefix + permission.ToString();
        actual.Should().Be(expected);
    }

    [Fact]
    public void Every_permission_yields_a_unique_policy_name()
    {
        var policyNames = Enum.GetValues<Permission>()
            .Select(p => PolicyPrefix + p.ToString())
            .ToList();

        policyNames.Should().OnlyHaveUniqueItems(
            "policy names are derived from enum names so two permissions with " +
            "the same name would collide silently in the policy provider.");
    }

    [Fact]
    public void Round_trip_policy_name_to_permission_succeeds_for_every_member()
    {
        // Mirrors what PermissionPolicyProvider.GetPolicyAsync does on the wire.
        foreach (var perm in Enum.GetValues<Permission>())
        {
            var policyName = PolicyPrefix + perm.ToString();
            policyName.StartsWith(PolicyPrefix, StringComparison.Ordinal)
                .Should().BeTrue();

            var stripped = policyName[PolicyPrefix.Length..];
            Enum.TryParse<Permission>(stripped, ignoreCase: false, out var parsed)
                .Should().BeTrue();
            parsed.Should().Be(perm);
        }
    }
}
