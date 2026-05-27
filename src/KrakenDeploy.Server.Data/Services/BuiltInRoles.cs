using KrakenDeploy.Server.Core.Domain.Security;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Permission sets for the built-in <see cref="Role"/>s seeded by
/// <c>BuiltInRoleSeeder</c>. Names mirror Octopus Deploy 1:1 so customers
/// migrating from Octopus see the same role inventory.
/// <para>
/// To change a built-in role's permissions: edit the array here, the seeder
/// re-applies it on the next server startup. To add a new built-in role:
/// add a constant + permission set + seeder call.
/// </para>
/// </summary>
internal static class BuiltInRoles
{
    // Stable IDs so role-assignment rows reference these by Guid even after
    // a re-seed. New built-in roles get new fixed Guids appended below.

    public static readonly Guid SystemAdministratorId = new("00000000-0000-0000-0001-000000000001");
    public static readonly Guid SpaceManagerId        = new("00000000-0000-0000-0001-000000000002");
    public static readonly Guid ProjectDeployerId     = new("00000000-0000-0000-0001-000000000003");
    public static readonly Guid ProjectContributorId  = new("00000000-0000-0000-0001-000000000004");
    public static readonly Guid ProjectViewerId       = new("00000000-0000-0000-0001-000000000005");
    public static readonly Guid TenantManagerId       = new("00000000-0000-0000-0001-000000000006");
    public static readonly Guid RunbookProducerId     = new("00000000-0000-0000-0001-000000000007");
    public static readonly Guid RunbookConsumerId     = new("00000000-0000-0000-0001-000000000008");
    public static readonly Guid SystemManagerId       = new("00000000-0000-0000-0001-000000000009");

    // ── Permission sets ────────────────────────────────────────────────────────
    // Declared BEFORE the All array so static field initialization order
    // populates them first.

    private static readonly Permission[] SpaceManagerPermissions =
    [
        // Space (the user can rename / configure their own space, but not
        // create new ones — that's a system-only permission)
        Permission.SpaceView, Permission.SpaceEdit,
        // Project Group
        Permission.ProjectGroupView, Permission.ProjectGroupCreate,
        Permission.ProjectGroupEdit, Permission.ProjectGroupDelete,
        // Project + Process
        Permission.ProjectView, Permission.ProjectCreate,
        Permission.ProjectEdit, Permission.ProjectDelete,
        Permission.ProjectExport, Permission.ProjectImport,
        Permission.ProcessView, Permission.ProcessEdit,
        // Release / Deployment
        Permission.ReleaseView, Permission.ReleaseCreate,
        Permission.ReleaseEdit, Permission.ReleaseDelete,
        Permission.DeploymentView, Permission.DeploymentCreate, Permission.DeploymentDelete,
        Permission.ArtifactView, Permission.ArtifactDownload,
        Permission.ArtifactCreate, Permission.ArtifactDelete,
        Permission.OfflineResultUpload,
        // Environment / Target
        Permission.EnvironmentView, Permission.EnvironmentCreate,
        Permission.EnvironmentEdit, Permission.EnvironmentDelete,
        Permission.MachineView, Permission.MachineCreate,
        Permission.MachineEdit, Permission.MachineDelete, Permission.MachineRetire,
        // Variables (incl. sensitive — Space Manager is full admin within Space)
        Permission.VariableView, Permission.VariableEdit,
        Permission.VariableViewUnscoped, Permission.VariableEditUnscoped,
        Permission.LibraryVariableSetView, Permission.LibraryVariableSetCreate,
        Permission.LibraryVariableSetEdit, Permission.LibraryVariableSetDelete,
        // Lifecycle / Channel
        Permission.LifecycleView, Permission.LifecycleCreate,
        Permission.LifecycleEdit, Permission.LifecycleDelete,
        Permission.ChannelView, Permission.ChannelCreate,
        Permission.ChannelEdit, Permission.ChannelDelete,
        // Tenant
        Permission.TenantView, Permission.TenantCreate,
        Permission.TenantEdit, Permission.TenantDelete,
        Permission.TagSetView, Permission.TagSetCreate,
        Permission.TagSetEdit, Permission.TagSetDelete,
        // Runbook
        Permission.RunbookView, Permission.RunbookEdit,
        Permission.RunbookRunView, Permission.RunbookRunCreate, Permission.RunbookRunDelete,
        // Step Templates
        Permission.StepTemplateView, Permission.StepTemplateCreate,
        Permission.StepTemplateEdit, Permission.StepTemplateDelete,
        // Package Library
        Permission.PackageView, Permission.PackageEdit, Permission.PackageDelete,
        // Task / Interruption
        Permission.TaskView, Permission.TaskCancel, Permission.TaskEdit, Permission.TaskRerun,
        Permission.InterruptionView, Permission.InterruptionViewSubmitResponsible,
        // Audit (within Space)
        Permission.EventView,
        // Teams + Roles within their Space (so they can grant access to others)
        Permission.TeamView, Permission.TeamCreate, Permission.TeamEdit, Permission.TeamDelete,
        Permission.RoleView,
        // User
        Permission.UserView, Permission.UserInvite,
        // API key (own keys only — admin needs ApiKeyViewAll/DeleteAll)
        Permission.ApiKeyView, Permission.ApiKeyCreate,
        Permission.ApiKeyEdit, Permission.ApiKeyDelete,
        // Ad-hoc agent actions (M11.E) — full Space admin can drive them;
        // the feature is additionally gated by the per-Space AdhocEnabled flag.
        Permission.AdhocActionsExecute,
    ];

    private static readonly Permission[] ProjectDeployerPermissions =
    [
        // Read project + process to know what's being deployed
        Permission.ProjectView, Permission.ProcessView,
        // Releases — view + create (often deployers cut releases)
        Permission.ReleaseView, Permission.ReleaseCreate,
        // Deployments — the headline ability
        Permission.DeploymentView, Permission.DeploymentCreate,
        // Artifacts — view + download (troubleshooting)
        Permission.ArtifactView, Permission.ArtifactDownload,
        // Offline drop result upload
        Permission.OfflineResultUpload,
        // Environments + targets — read-only
        Permission.EnvironmentView, Permission.MachineView,
        // Variables — non-sensitive only
        Permission.VariableView,
        Permission.LibraryVariableSetView,
        // Lifecycle, channel, tenant — read-only
        Permission.LifecycleView, Permission.ChannelView,
        Permission.TenantView, Permission.TagSetView,
        // Step templates + packages — needed to display step config
        Permission.StepTemplateView, Permission.PackageView,
        // Tasks — see + retry own deployments
        Permission.TaskView, Permission.TaskRerun,
        Permission.InterruptionView, Permission.InterruptionViewSubmitResponsible,
        // Audit (own actions visible in Space audit)
        Permission.EventView,
        // Project Group view (sidebar nav)
        Permission.ProjectGroupView,
    ];

    private static readonly Permission[] ProjectContributorPermissions =
    [
        Permission.ProjectGroupView,
        Permission.ProjectView, Permission.ProjectEdit,
        Permission.ProcessView, Permission.ProcessEdit,
        Permission.ReleaseView, Permission.ReleaseCreate, Permission.ReleaseEdit,
        Permission.DeploymentView,
        Permission.ArtifactView, Permission.ArtifactDownload,
        Permission.EnvironmentView, Permission.MachineView,
        Permission.VariableView, Permission.VariableEdit,
        Permission.LibraryVariableSetView,
        Permission.LifecycleView, Permission.ChannelView,
        Permission.ChannelCreate, Permission.ChannelEdit,
        Permission.TenantView, Permission.TagSetView,
        Permission.RunbookView, Permission.RunbookEdit,
        Permission.RunbookRunView,
        Permission.StepTemplateView,
        Permission.PackageView, Permission.PackageEdit,
        Permission.TaskView,
        Permission.InterruptionView,
        Permission.EventView,
    ];

    private static readonly Permission[] ProjectViewerPermissions =
    [
        Permission.ProjectGroupView,
        Permission.ProjectView, Permission.ProcessView,
        Permission.ReleaseView, Permission.DeploymentView,
        Permission.ArtifactView, Permission.ArtifactDownload,
        Permission.EnvironmentView, Permission.MachineView,
        Permission.VariableView,
        Permission.LibraryVariableSetView,
        Permission.LifecycleView, Permission.ChannelView,
        Permission.TenantView, Permission.TagSetView,
        Permission.RunbookView, Permission.RunbookRunView,
        Permission.StepTemplateView, Permission.PackageView,
        Permission.TaskView, Permission.InterruptionView,
        Permission.EventView,
    ];

    private static readonly Permission[] TenantManagerPermissions =
    [
        Permission.TenantView, Permission.TenantCreate, Permission.TenantEdit, Permission.TenantDelete,
        Permission.TagSetView, Permission.TagSetCreate, Permission.TagSetEdit, Permission.TagSetDelete,
        Permission.ProjectView, Permission.EnvironmentView, Permission.MachineView,
        Permission.EventView,
    ];

    private static readonly Permission[] RunbookProducerPermissions =
    [
        Permission.ProjectGroupView, Permission.ProjectView,
        Permission.RunbookView, Permission.RunbookEdit, Permission.RunbookRunView,
        Permission.EnvironmentView, Permission.MachineView,
        Permission.VariableView, Permission.LibraryVariableSetView,
        Permission.StepTemplateView, Permission.PackageView,
        Permission.TaskView, Permission.EventView,
    ];

    private static readonly Permission[] RunbookConsumerPermissions =
    [
        Permission.ProjectGroupView, Permission.ProjectView,
        Permission.RunbookView, Permission.RunbookRunView, Permission.RunbookRunCreate,
        Permission.EnvironmentView, Permission.MachineView,
        Permission.VariableView,
        Permission.TaskView, Permission.InterruptionView, Permission.InterruptionViewSubmitResponsible,
        Permission.EventView,
    ];

    /// <summary>
    /// System Manager = the "delegated admin" tier (M13.C.5). Every
    /// explicit permission in the enum EXCEPT <see cref="Permission.AdministerSystem"/>.
    /// <para>
    /// Why exclude AdministerSystem? It's the god-mode catch-all that
    /// <see cref="IPermissionEvaluator"/> treats as "implies every other
    /// permission everywhere, including ones added in future releases."
    /// Granting AdministerSystem would auto-grant future high-risk
    /// permissions (encryption-master-key rotation in M13.D.2, signing-
    /// key revocation in M13.D.1) the moment they ship — without an
    /// admin's explicit decision to expand the role.
    /// </para>
    /// <para>
    /// What System Manager CAN do: edit users, manage teams + roles,
    /// configure identity providers, edit license, manage every Space's
    /// projects + deployments + variables (sensitive included), read
    /// cross-Space audit log, manage every Space's AI settings + every
    /// installed step package. Essentially everything an operator might
    /// delegate to a junior admin.
    /// </para>
    /// <para>
    /// What System Manager CANNOT do: anything we tag with a new
    /// "danger zone" permission added AFTER this release without
    /// updating <see cref="SystemManagerPermissions"/>. That's the safety
    /// property — operators reviewing the role on a future release see
    /// exactly which new permissions need a conscious grant decision.
    /// </para>
    /// </summary>
    private static readonly Permission[] SystemManagerPermissions =
    [
        // ── Cross-Space + server settings ──────────────────────────────────
        Permission.ConfigureServer,
        Permission.SpaceView, Permission.SpaceCreate, Permission.SpaceEdit, Permission.SpaceDelete,
        Permission.UserView, Permission.UserEdit, Permission.UserInvite, Permission.UserChangePassword,
        Permission.TeamView, Permission.TeamCreate, Permission.TeamEdit, Permission.TeamDelete,
        Permission.RoleView, Permission.RoleCreate, Permission.RoleEdit, Permission.RoleDelete,
        Permission.EventViewUnscoped,

        // ── Project Group ─────────────────────────────────────────────────
        Permission.ProjectGroupView, Permission.ProjectGroupCreate,
        Permission.ProjectGroupEdit, Permission.ProjectGroupDelete,

        // ── Project / Process ─────────────────────────────────────────────
        Permission.ProjectView, Permission.ProjectCreate,
        Permission.ProjectEdit, Permission.ProjectDelete,
        Permission.ProjectExport, Permission.ProjectImport,
        Permission.ProcessView, Permission.ProcessEdit,

        // ── Release / Deployment ──────────────────────────────────────────
        Permission.ReleaseView, Permission.ReleaseCreate,
        Permission.ReleaseEdit, Permission.ReleaseDelete,
        Permission.DeploymentView, Permission.DeploymentCreate, Permission.DeploymentDelete,
        Permission.ArtifactView, Permission.ArtifactDownload,
        Permission.ArtifactCreate, Permission.ArtifactDelete,
        Permission.OfflineResultUpload,

        // ── Environment / Target ──────────────────────────────────────────
        Permission.EnvironmentView, Permission.EnvironmentCreate,
        Permission.EnvironmentEdit, Permission.EnvironmentDelete,
        Permission.MachineView, Permission.MachineCreate,
        Permission.MachineEdit, Permission.MachineDelete, Permission.MachineRetire,

        // ── Variables (incl. sensitive — System Manager is system-wide admin) ─
        Permission.VariableView, Permission.VariableEdit,
        Permission.VariableViewUnscoped, Permission.VariableEditUnscoped,
        Permission.LibraryVariableSetView, Permission.LibraryVariableSetCreate,
        Permission.LibraryVariableSetEdit, Permission.LibraryVariableSetDelete,

        // ── Lifecycle / Channel ───────────────────────────────────────────
        Permission.LifecycleView, Permission.LifecycleCreate,
        Permission.LifecycleEdit, Permission.LifecycleDelete,
        Permission.ChannelView, Permission.ChannelCreate,
        Permission.ChannelEdit, Permission.ChannelDelete,

        // ── Tenant ────────────────────────────────────────────────────────
        Permission.TenantView, Permission.TenantCreate,
        Permission.TenantEdit, Permission.TenantDelete,
        Permission.TagSetView, Permission.TagSetCreate,
        Permission.TagSetEdit, Permission.TagSetDelete,

        // ── Runbook ───────────────────────────────────────────────────────
        Permission.RunbookView, Permission.RunbookEdit,
        Permission.RunbookRunView, Permission.RunbookRunCreate, Permission.RunbookRunDelete,

        // ── Step Templates + Step Packages ───────────────────────────────
        Permission.StepTemplateView, Permission.StepTemplateCreate,
        Permission.StepTemplateEdit, Permission.StepTemplateDelete,
        Permission.StepPackageView, Permission.StepPackageManage,

        // ── Package Library ──────────────────────────────────────────────
        Permission.PackageView, Permission.PackageEdit, Permission.PackageDelete,

        // ── Task / Interruption ──────────────────────────────────────────
        Permission.TaskView, Permission.TaskCancel, Permission.TaskEdit, Permission.TaskRerun,
        Permission.InterruptionView, Permission.InterruptionViewSubmitResponsible,

        // ── Audit ────────────────────────────────────────────────────────
        Permission.EventView,

        // ── API Key (admin view + own) ───────────────────────────────────
        Permission.ApiKeyView, Permission.ApiKeyCreate,
        Permission.ApiKeyEdit, Permission.ApiKeyDelete,
        Permission.ApiKeyViewAll, Permission.ApiKeyDeleteAll,

        // ── Identity Provider ────────────────────────────────────────────
        Permission.IdentityProviderView, Permission.IdentityProviderCreate,
        Permission.IdentityProviderEdit, Permission.IdentityProviderDelete,

        // ── AI Settings ──────────────────────────────────────────────────
        Permission.SpaceAiSettingsView, Permission.SpaceAiSettingsManage,

        // ── Deployment freezes (M13.F.2) ─────────────────────────────────
        Permission.DeploymentFreezeView, Permission.DeploymentFreezeManage,
        Permission.DeploymentFreezeOverride,

        // ── Subscriptions (M13.B.2/3) ───────────────────────────────────
        Permission.SubscriptionView, Permission.SubscriptionManage,

        // ── Maintenance mode (M13.A.3) ──────────────────────────────────
        // SystemManager runs the maintenance work itself — pause window
        // exists to keep normal users out, not to gate the operator.
        Permission.BypassMaintenance,

        // ── Ad-hoc agent actions (M11.E) ────────────────────────────────
        // Delegated admins can drive ad-hoc actions; still gated by the
        // per-Space AdhocEnabled flag + AI budget + the per-iteration
        // approval + signing pipeline.
        Permission.AdhocActionsExecute,
    ];

    // ── Built-in role definitions (declared LAST — depends on permission sets above) ─

    public static readonly BuiltInRoleDef[] All =
    [
        new(SystemAdministratorId, "System Administrator",
            "God mode. Implies every other permission everywhere. Cannot be " +
            "scope-restricted; always granted system-wide.",
            permissions: [Permission.AdministerSystem],
            isSystemOnly: true),

        new(SystemManagerId, "System Manager",
            "Delegated-admin tier. System-wide access to everything an " +
            "operator might delegate to a junior admin — manage users, " +
            "teams, roles, IdPs, edit the license, manage every Space's " +
            "projects + variables + step packages, read cross-Space audit. " +
            "EXCLUDES AdministerSystem (the god-mode catch-all) so future " +
            "high-risk permissions (encryption master-key rotation, signing-" +
            "key revocation) require an explicit grant decision when they " +
            "ship — they're not auto-granted by virtue of holding this role.",
            permissions: SystemManagerPermissions,
            isSystemOnly: true),

        new(SpaceManagerId, "Space Manager",
            "Full administrative control within a single Space — can create, " +
            "edit, and delete every object and grant any permission to other " +
            "teams. Cannot manage system-level objects (other Spaces, Identity " +
            "Providers, license).",
            permissions: SpaceManagerPermissions),

        new(ProjectDeployerId, "Project Deployer",
            "Creates releases and runs deployments against projects in the " +
            "assignment's scope. Read-only on everything else needed to do that " +
            "(variables non-sensitive, environments, lifecycles, channels, " +
            "step templates, packages).",
            permissions: ProjectDeployerPermissions),

        new(ProjectContributorId, "Project Contributor",
            "Edits projects, processes, runbooks, and variables; can create " +
            "releases. Cannot run deployments — pairs with Project Deployer or " +
            "a release-promotion flow.",
            permissions: ProjectContributorPermissions),

        new(ProjectViewerId, "Project Viewer",
            "Read-only access to projects, processes, releases, deployments, " +
            "variables (non-sensitive), runbooks, and audit within the " +
            "assignment's scope.",
            permissions: ProjectViewerPermissions),

        new(TenantManagerId, "Tenant Manager",
            "Manages Tenants and their Tag Sets within the assignment's scope. " +
            "Read-only on Projects and Environments (needed to wire tenants up " +
            "to deployment targets).",
            permissions: TenantManagerPermissions),

        new(RunbookProducerId, "Runbook Producer",
            "Authors and edits runbooks within the assignment's scope. Can " +
            "view executions but not trigger them — separation of duties from " +
            "Runbook Consumer.",
            permissions: RunbookProducerPermissions),

        new(RunbookConsumerId, "Runbook Consumer",
            "Triggers runbook executions within the assignment's scope. " +
            "Read-only on the runbook definitions themselves.",
            permissions: RunbookConsumerPermissions),
    ];
}

/// <summary>Definition of a built-in <see cref="Role"/> — drives the seeder.</summary>
internal sealed record BuiltInRoleDef(
    Guid Id,
    string Name,
    string Description,
    Permission[] permissions,
    bool isSystemOnly = false)
{
    public IReadOnlyList<Permission> Permissions { get; } = permissions;
    public bool IsSystemOnly { get; } = isSystemOnly;
}
