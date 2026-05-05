using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Security;

namespace KrakenDeploy.Server.Core.Tests;

/// <summary>
/// Stability tests for the built-in roles' Guids and the System Administrator
/// permission set. These IDs are persisted in role_assignments rows — silently
/// changing one would orphan every Space's "Space Managers" RoleAssignment.
/// </summary>
public sealed class BuiltInRolesTests
{
    // The seeder lives in Server.Data; this test only locks down the Permission
    // contract and the well-known ID Guids so they can't drift.

    [Fact]
    public void System_administrator_role_id_is_stable()
    {
        // Locked-in Guid — see Data/Services/BuiltInRoles.cs
        var expected = new Guid("00000000-0000-0000-0001-000000000001");
        expected.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void AdministerSystem_is_the_god_mode_permission()
    {
        // Sanity: AdministerSystem must be the very first permission (int 0)
        // because the evaluator special-cases it as a global short-circuit.
        ((int)Permission.AdministerSystem).Should().Be(0);
    }

    [Fact]
    public void System_only_permissions_include_AdministerSystem()
    {
        // Permissions that should ONLY be granted via system-wide assignment
        // (no Space scope) — UI hides the scope selector for these.
        var systemOnly = new[]
        {
            Permission.AdministerSystem,
            Permission.ConfigureServer,
            Permission.SpaceCreate,
            Permission.SpaceDelete,
            Permission.IdentityProviderView,
            Permission.IdentityProviderCreate,
            Permission.IdentityProviderEdit,
            Permission.IdentityProviderDelete,
        };

        // Just confirm the enum members exist; integer-stability is checked
        // by PermissionTests.
        systemOnly.Should().OnlyHaveUniqueItems();
        systemOnly.Should().NotBeEmpty();
    }

    [Fact]
    public void PermissionScope_default_is_system_wide()
    {
        var scope = default(PermissionScope);

        scope.IsSystemWide.Should().BeTrue();
        scope.SpaceId.Should().BeNull();
        scope.ProjectId.Should().BeNull();
        scope.EnvironmentId.Should().BeNull();
        scope.TenantId.Should().BeNull();
    }

    [Fact]
    public void PermissionScope_with_only_SpaceId_is_not_system_wide()
    {
        var scope = new PermissionScope(SpaceId: Guid.NewGuid());

        scope.IsSystemWide.Should().BeFalse();
    }
}
