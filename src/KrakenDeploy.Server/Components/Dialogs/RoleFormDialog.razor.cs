using KrakenDeploy.Server.Core.Domain.Security;

namespace KrakenDeploy.Server.Components.Dialogs;

public partial class RoleFormDialog
{
    private static readonly IReadOnlyList<PermissionGroup> _permissionGroups =
        BuildPermissionGroups();

    private static List<PermissionGroup> BuildPermissionGroups()
    {
        static string DomainFor(int value) => value switch
        {
            < 100  => "System",
            < 200  => "Project Group",
            < 300  => "Project / Process",
            < 400  => "Release / Deployment",
            < 500  => "Environment / Target",
            < 600  => "Variables",
            < 700  => "Lifecycle / Channel",
            < 800  => "Tenant",
            < 900  => "Runbook",
            < 1000 => "Step Templates",
            < 1100 => "Package Library",
            < 1200 => "Task / Interruption",
            < 1300 => "Audit",
            < 1400 => "API Key",
            _      => "Identity Provider",
        };

        return Enum.GetValues<Permission>()
            .GroupBy(p => DomainFor((int)p))
            .OrderBy(g => (int)g.First())
            .Select(g => new PermissionGroup(g.Key, g.ToList()))
            .ToList();
    }

    private sealed record PermissionGroup(
        string Domain, IReadOnlyList<Permission> Permissions);
}
