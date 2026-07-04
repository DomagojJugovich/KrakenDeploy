using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Security;

namespace KrakenDeploy.Server.Core.Tests;

/// <summary>
/// Stability tests for <see cref="Permission"/>. Integer values of these enum
/// members are persisted in <c>roles.granted_permissions</c> JSONB arrays —
/// silently renumbering one corrupts every Role definition. These tests fail
/// loudly the moment a value drifts.
/// </summary>
public sealed class PermissionTests
{
    /// <summary>
    /// Locks in the integer values for every Permission that has shipped.
    /// Adding a new Permission means appending a row here. Changing an
    /// existing value means a migration to remap stored Role permission
    /// arrays — and a very deliberate choice to do so.
    /// </summary>
    [Theory]
    // System
    [InlineData(Permission.AdministerSystem,      0)]
    [InlineData(Permission.ConfigureServer,       1)]
    [InlineData(Permission.SpaceView,            10)]
    [InlineData(Permission.SpaceCreate,          11)]
    [InlineData(Permission.SpaceEdit,            12)]
    [InlineData(Permission.SpaceDelete,          13)]
    [InlineData(Permission.UserView,             20)]
    [InlineData(Permission.UserEdit,             21)]
    [InlineData(Permission.UserInvite,           22)]
    [InlineData(Permission.UserChangePassword,   23)]
    [InlineData(Permission.TeamView,             30)]
    [InlineData(Permission.TeamCreate,           31)]
    [InlineData(Permission.TeamEdit,             32)]
    [InlineData(Permission.TeamDelete,           33)]
    [InlineData(Permission.RoleView,             40)]
    [InlineData(Permission.RoleCreate,           41)]
    [InlineData(Permission.RoleEdit,             42)]
    [InlineData(Permission.RoleDelete,           43)]
    [InlineData(Permission.EventViewUnscoped,    50)]
    // Project Group
    [InlineData(Permission.ProjectGroupView,     100)]
    [InlineData(Permission.ProjectGroupCreate,   101)]
    [InlineData(Permission.ProjectGroupEdit,     102)]
    [InlineData(Permission.ProjectGroupDelete,   103)]
    // Project / Process
    [InlineData(Permission.ProjectView,          200)]
    [InlineData(Permission.ProjectCreate,        201)]
    [InlineData(Permission.ProjectEdit,          202)]
    [InlineData(Permission.ProjectDelete,        203)]
    [InlineData(Permission.ProjectExport,        210)]
    [InlineData(Permission.ProjectImport,        211)]
    [InlineData(Permission.ProcessView,          220)]
    [InlineData(Permission.ProcessEdit,          221)]
    // Release / Deployment
    [InlineData(Permission.ReleaseView,          300)]
    [InlineData(Permission.ReleaseCreate,        301)]
    [InlineData(Permission.ReleaseEdit,          302)]
    [InlineData(Permission.ReleaseDelete,        303)]
    [InlineData(Permission.DeploymentView,       310)]
    [InlineData(Permission.DeploymentCreate,     311)]
    [InlineData(Permission.DeploymentDelete,     312)]
    [InlineData(Permission.ArtifactView,         320)]
    [InlineData(Permission.ArtifactDownload,     321)]
    [InlineData(Permission.ArtifactCreate,       322)]
    [InlineData(Permission.ArtifactDelete,       323)]
    [InlineData(Permission.OfflineResultUpload,  330)]
    // Environment / Target
    [InlineData(Permission.EnvironmentView,      400)]
    [InlineData(Permission.EnvironmentCreate,    401)]
    [InlineData(Permission.EnvironmentEdit,      402)]
    [InlineData(Permission.EnvironmentDelete,    403)]
    [InlineData(Permission.MachineView,          410)]
    [InlineData(Permission.MachineCreate,        411)]
    [InlineData(Permission.MachineEdit,          412)]
    [InlineData(Permission.MachineDelete,        413)]
    [InlineData(Permission.MachineRetire,        414)]
    // Variables
    [InlineData(Permission.VariableView,             500)]
    [InlineData(Permission.VariableEdit,             501)]
    [InlineData(Permission.VariableViewUnscoped,     502)]
    [InlineData(Permission.VariableEditUnscoped,     503)]
    [InlineData(Permission.LibraryVariableSetView,   510)]
    [InlineData(Permission.LibraryVariableSetCreate, 511)]
    [InlineData(Permission.LibraryVariableSetEdit,   512)]
    [InlineData(Permission.LibraryVariableSetDelete, 513)]
    // Lifecycle / Channel
    [InlineData(Permission.LifecycleView,        600)]
    [InlineData(Permission.LifecycleCreate,      601)]
    [InlineData(Permission.LifecycleEdit,        602)]
    [InlineData(Permission.LifecycleDelete,      603)]
    [InlineData(Permission.ChannelView,          610)]
    [InlineData(Permission.ChannelCreate,        611)]
    [InlineData(Permission.ChannelEdit,          612)]
    [InlineData(Permission.ChannelDelete,        613)]
    // Tenant
    [InlineData(Permission.TenantView,           700)]
    [InlineData(Permission.TenantCreate,         701)]
    [InlineData(Permission.TenantEdit,           702)]
    [InlineData(Permission.TenantDelete,         703)]
    [InlineData(Permission.TagSetView,           710)]
    [InlineData(Permission.TagSetCreate,         711)]
    [InlineData(Permission.TagSetEdit,           712)]
    [InlineData(Permission.TagSetDelete,         713)]
    // Runbook
    [InlineData(Permission.RunbookView,          800)]
    [InlineData(Permission.RunbookEdit,          801)]
    [InlineData(Permission.RunbookRunView,       810)]
    [InlineData(Permission.RunbookRunCreate,     811)]
    [InlineData(Permission.RunbookRunDelete,     812)]
    // Step Templates
    [InlineData(Permission.StepTemplateView,     900)]
    [InlineData(Permission.StepTemplateCreate,   901)]
    [InlineData(Permission.StepTemplateEdit,     902)]
    [InlineData(Permission.StepTemplateDelete,   903)]
    // Step Packages (Phase D)
    [InlineData(Permission.StepPackageView,      950)]
    [InlineData(Permission.StepPackageManage,    951)]
    // Package Library
    [InlineData(Permission.PackageView,         1000)]
    [InlineData(Permission.PackageEdit,         1001)]
    [InlineData(Permission.PackageDelete,       1002)]
    // Task / Interruption
    [InlineData(Permission.TaskView,            1100)]
    [InlineData(Permission.TaskCancel,          1101)]
    [InlineData(Permission.TaskEdit,            1102)]
    [InlineData(Permission.TaskRerun,           1103)]
    [InlineData(Permission.InterruptionView,    1110)]
    [InlineData(Permission.InterruptionViewSubmitResponsible, 1111)]
    // Audit
    [InlineData(Permission.EventView,           1200)]
    // API Key
    [InlineData(Permission.ApiKeyView,          1300)]
    [InlineData(Permission.ApiKeyCreate,        1301)]
    [InlineData(Permission.ApiKeyEdit,          1302)]
    [InlineData(Permission.ApiKeyDelete,        1303)]
    [InlineData(Permission.ApiKeyViewAll,       1310)]
    [InlineData(Permission.ApiKeyDeleteAll,     1311)]
    [InlineData(Permission.ApiKeyCreateOthers,  1312)]
    // Identity Provider
    [InlineData(Permission.IdentityProviderView,   1400)]
    [InlineData(Permission.IdentityProviderCreate, 1401)]
    [InlineData(Permission.IdentityProviderEdit,   1402)]
    [InlineData(Permission.IdentityProviderDelete, 1403)]
    // AI Settings (M11.A.6)
    [InlineData(Permission.SpaceAiSettingsView,    1500)]
    [InlineData(Permission.SpaceAiSettingsManage,  1501)]
    // Deployment Freezes (M13.F.2)
    [InlineData(Permission.DeploymentFreezeView,     1600)]
    [InlineData(Permission.DeploymentFreezeManage,   1601)]
    [InlineData(Permission.DeploymentFreezeOverride, 1602)]
    // Subscriptions (M13.B.2/3)
    [InlineData(Permission.SubscriptionView,         1700)]
    [InlineData(Permission.SubscriptionManage,       1701)]
    // Maintenance mode (M13.A.3)
    [InlineData(Permission.BypassMaintenance,        1800)]
    // Ad-hoc agent actions (M11.E)
    [InlineData(Permission.AdhocActionsExecute,      1900)]
    public void Permission_integer_values_are_stable(Permission perm, int expectedValue)
    {
        ((int)perm).Should().Be(expectedValue,
            $"changing the integer value of {perm} would corrupt every persisted Role.");
    }

    [Fact]
    public void Every_permission_has_a_unique_integer_value()
    {
        var values = Enum.GetValues<Permission>().Select(p => (int)p).ToList();
        values.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_enum_member_is_covered_by_the_stability_test()
    {
        // If you add a new Permission, you must also add an InlineData row to
        // Permission_integer_values_are_stable above. This test fails until
        // you do.
        var allMembers = Enum.GetNames<Permission>().Length;

        // Count from the [Theory]/[InlineData] entries above. If you add new
        // ones, bump this number.
        const int expectedCoverage = 117;

        allMembers.Should().Be(expectedCoverage,
            "the stability theory must cover every Permission member; " +
            "if you added a new Permission, add a matching InlineData row " +
            "to Permission_integer_values_are_stable and update this count.");
    }
}
