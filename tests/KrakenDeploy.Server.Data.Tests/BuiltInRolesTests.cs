using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Pin the contract for built-in roles seeded into the database. The roles
/// have stable Guids that role-assignment rows reference — accidentally
/// renaming, deleting, or reshuffling these breaks every existing assignment.
/// </summary>
public sealed class BuiltInRolesTests
{
    [Fact]
    public void All_built_in_roles_have_stable_unique_ids()
    {
        var ids = BuiltInRoles.All.Select(r => r.Id).ToList();
        ids.Should().OnlyHaveUniqueItems(
            "RoleAssignment rows reference these by Guid — a collision " +
            "would silently re-target every assignment to the wrong role");
        ids.Should().AllSatisfy(id => id.Should().NotBe(Guid.Empty));
    }

    [Fact]
    public void All_built_in_role_names_are_unique()
    {
        var names = BuiltInRoles.All.Select(r => r.Name).ToList();
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void SystemAdministrator_has_only_AdministerSystem()
    {
        // The IPermissionEvaluator contract is that AdministerSystem
        // implies every other permission — the role's permission list is
        // intentionally minimal to keep the implication path the single
        // source of truth.
        var sysAdmin = BuiltInRoles.All.Single(r => r.Id == BuiltInRoles.SystemAdministratorId);
        sysAdmin.Permissions.Should().ContainSingle()
            .Which.Should().Be(Permission.AdministerSystem);
        sysAdmin.IsSystemOnly.Should().BeTrue("god-mode is never scope-restricted");
    }

    [Fact]
    public void SystemManager_does_NOT_carry_AdministerSystem()
    {
        // Critical safety property — see the comment on SystemManagerPermissions
        // in BuiltInRoles.cs. Granting AdministerSystem would auto-grant every
        // future permission added to the enum (encryption master-key rotation,
        // signing-key revocation, etc.) the moment they ship, bypassing the
        // explicit-grant decision the delegated-admin tier is supposed to
        // require.
        var sysManager = BuiltInRoles.All.Single(r => r.Id == BuiltInRoles.SystemManagerId);
        sysManager.Permissions.Should().NotContain(Permission.AdministerSystem,
            "SystemManager must NOT carry the god-mode catch-all — that's " +
            "the entire point of the delegated-admin tier");
        sysManager.IsSystemOnly.Should().BeTrue(
            "system-wide admin tier is never scope-restricted to one Space");
    }

    [Fact]
    public void SystemManager_has_ConfigureServer_but_not_AdministerSystem()
    {
        var sysManager = BuiltInRoles.All.Single(r => r.Id == BuiltInRoles.SystemManagerId);

        sysManager.Permissions.Should().Contain(Permission.ConfigureServer,
            "delegated admins manage the license + OIDC + server settings");
        sysManager.Permissions.Should().NotContain(Permission.AdministerSystem,
            "the god-mode catch-all stays exclusive to SystemAdministrator");
    }

    [Fact]
    public void SystemManager_covers_every_explicit_permission_except_AdministerSystem()
    {
        // The contract: SystemManager is "everything that exists in the
        // Permission enum today, EXCEPT god-mode". If a new Permission is
        // added to the enum and this test fails, the operator is now
        // forced to make an explicit decision about whether SystemManager
        // should automatically gain it — that's the safety property.
        var sysManager = BuiltInRoles.All.Single(r => r.Id == BuiltInRoles.SystemManagerId);

        var allPermissions      = Enum.GetValues<Permission>();
        var expectedPermissions = allPermissions
            .Except([Permission.AdministerSystem])
            .ToHashSet();

        sysManager.Permissions.Should().BeEquivalentTo(expectedPermissions,
            "SystemManager covers every explicit permission except " +
            "AdministerSystem. If you added a Permission and this fails, " +
            "decide explicitly whether SystemManager should hold it, then " +
            "update either SystemManagerPermissions or this test's exclusion list");
    }

    [Fact]
    public void All_known_roles_present_in_All_array()
    {
        // Cheap sanity: every public Guid constant on BuiltInRoles appears
        // exactly once in BuiltInRoles.All. Catches "added a Guid constant
        // but forgot to wire it into the seeder array".
        var declaredIds = new[]
        {
            BuiltInRoles.SystemAdministratorId,
            BuiltInRoles.SystemManagerId,
            BuiltInRoles.SpaceManagerId,
            BuiltInRoles.ProjectDeployerId,
            BuiltInRoles.ProjectContributorId,
            BuiltInRoles.ProjectViewerId,
            BuiltInRoles.TenantManagerId,
            BuiltInRoles.RunbookProducerId,
            BuiltInRoles.RunbookConsumerId,
        };

        var allIds = BuiltInRoles.All.Select(r => r.Id).ToHashSet();

        foreach (var id in declaredIds)
        {
            allIds.Should().Contain(id,
                $"role Guid {id} declared as a public constant but not " +
                "wired into BuiltInRoles.All — the seeder will never " +
                "create the actual Role row");
        }
    }
}
