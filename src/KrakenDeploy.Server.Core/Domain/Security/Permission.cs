using System.Diagnostics.CodeAnalysis;

namespace KrakenDeploy.Server.Core.Domain.Security;

/// <summary>
/// Granular permission atoms. Roles bundle these into named sets; Role
/// Assignments grant a Role to a Team within a scope (Project Groups / Projects
/// / Environments / Tenants / Tenant Tags).
/// <para>
/// Vocabulary mirrors Octopus Deploy 1:1 so an Octopus customer's mental model
/// maps directly onto KrakenDeploy. Where Octopus has e.g.
/// <c>VariableViewUnscoped</c> / <c>VariableEditUnscoped</c> for sensitive
/// values, KrakenDeploy uses the same names.
/// </para>
/// <para>
/// <b>Stability contract:</b> every enum value carries an explicit integer.
/// Those integers are persisted in <c>roles.granted_permissions</c> JSONB
/// arrays — changing them silently corrupts every existing Role. Add new
/// values by appending; never reshuffle. Each domain is allocated a 100-value
/// block so new permissions for an entity slot in next to its peers.
/// </para>
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Permission is the standard domain term — Octopus parity.")]
public enum Permission
{
    // ── System / cross-Space (0–99) ─────────────────────────────────────────

    /// <summary>God mode — implies every other permission everywhere. Granted
    /// to <c>SystemAdministrator</c> role only.</summary>
    AdministerSystem      = 0,

    /// <summary>Server settings, license, OIDC config, agent auto-update.</summary>
    ConfigureServer       = 1,

    SpaceView             = 10,
    SpaceCreate           = 11,
    SpaceEdit             = 12,
    SpaceDelete           = 13,

    UserView              = 20,
    UserEdit              = 21,
    UserInvite            = 22,
    UserChangePassword    = 23,

    TeamView              = 30,
    TeamCreate            = 31,
    TeamEdit              = 32,
    TeamDelete            = 33,

    /// <summary>View custom-role definitions.</summary>
    RoleView              = 40,
    RoleCreate            = 41,
    RoleEdit              = 42,
    RoleDelete            = 43,

    /// <summary>Full audit log across Spaces (cross-Space). Differs from
    /// scope-respecting <see cref="EventView"/>.</summary>
    EventViewUnscoped     = 50,

    // ── Project Group (100–199) ─────────────────────────────────────────────

    ProjectGroupView      = 100,
    ProjectGroupCreate    = 101,
    ProjectGroupEdit      = 102,
    ProjectGroupDelete    = 103,

    // ── Project / Process (200–299) ─────────────────────────────────────────

    ProjectView           = 200,
    ProjectCreate         = 201,
    ProjectEdit           = 202,
    ProjectDelete         = 203,
    ProjectExport         = 210,
    ProjectImport         = 211,

    /// <summary>View the deployment-process step list.</summary>
    ProcessView           = 220,
    /// <summary>Edit the deployment-process step list.</summary>
    ProcessEdit           = 221,

    // ── Release / Deployment (300–399) ──────────────────────────────────────

    ReleaseView           = 300,
    ReleaseCreate         = 301,
    ReleaseEdit           = 302,
    ReleaseDelete         = 303,

    DeploymentView        = 310,
    DeploymentCreate      = 311,
    DeploymentDelete      = 312,

    ArtifactView          = 320,
    ArtifactDownload      = 321,
    ArtifactCreate        = 322,
    ArtifactDelete        = 323,

    /// <summary>Upload a result bundle for an offline-drop deployment (M8).</summary>
    OfflineResultUpload   = 330,

    // ── Environment / Target (400–499) ──────────────────────────────────────

    EnvironmentView       = 400,
    EnvironmentCreate     = 401,
    EnvironmentEdit       = 402,
    EnvironmentDelete     = 403,

    /// <summary><c>DeploymentTarget</c> is called "Machine" in Octopus
    /// terminology — kept for vocabulary parity.</summary>
    MachineView           = 410,
    MachineCreate         = 411,
    MachineEdit           = 412,
    MachineDelete         = 413,
    /// <summary>Disable / mark a target offline.</summary>
    MachineRetire         = 414,

    // ── Variables (500–599) ─────────────────────────────────────────────────

    VariableView          = 500,
    VariableEdit          = 501,
    /// <summary>View sensitive (decrypted) variables. Distinct from
    /// <see cref="VariableView"/> — Octopus parity.</summary>
    VariableViewUnscoped  = 502,
    /// <summary>Edit sensitive variables.</summary>
    VariableEditUnscoped  = 503,

    LibraryVariableSetView   = 510,
    LibraryVariableSetCreate = 511,
    LibraryVariableSetEdit   = 512,
    LibraryVariableSetDelete = 513,

    // ── Lifecycle / Channel (600–699) ───────────────────────────────────────

    LifecycleView         = 600,
    LifecycleCreate       = 601,
    LifecycleEdit         = 602,
    LifecycleDelete       = 603,

    ChannelView           = 610,
    ChannelCreate         = 611,
    ChannelEdit           = 612,
    ChannelDelete         = 613,

    // ── Tenant (700–799) ────────────────────────────────────────────────────

    TenantView            = 700,
    TenantCreate          = 701,
    TenantEdit            = 702,
    TenantDelete          = 703,

    TagSetView            = 710,
    TagSetCreate          = 711,
    TagSetEdit            = 712,
    TagSetDelete          = 713,

    // ── Runbook (800–899) ───────────────────────────────────────────────────

    RunbookView           = 800,
    RunbookEdit           = 801,

    RunbookRunView        = 810,
    /// <summary>Trigger a runbook execution.</summary>
    RunbookRunCreate      = 811,
    RunbookRunDelete      = 812,

    // ── Step Templates (900–999) ────────────────────────────────────────────

    StepTemplateView      = 900,
    StepTemplateCreate    = 901,
    StepTemplateEdit      = 902,
    StepTemplateDelete    = 903,

    // ── Step Packages (Phase D — .kdeploy-step plugins) ─────────────────────

    StepPackageView       = 950,
    /// <summary>Upload, install from catalog, and uninstall step packages.</summary>
    StepPackageManage     = 951,

    // ── Package Library (1000–1099) ─────────────────────────────────────────

    PackageView           = 1000,
    /// <summary>Upload a package version.</summary>
    PackageEdit           = 1001,
    PackageDelete         = 1002,

    // ── Task / Interruption (1100–1199) ─────────────────────────────────────

    TaskView              = 1100,
    TaskCancel            = 1101,
    TaskEdit              = 1102,
    TaskRerun             = 1103,

    InterruptionView                  = 1110,
    /// <summary>Approve / reject a manual-intervention step.</summary>
    InterruptionViewSubmitResponsible = 1111,

    // ── Audit (1200–1299) ───────────────────────────────────────────────────

    /// <summary>Audit log entries within the scopes the user has access to.
    /// Cross-Space audit needs <see cref="EventViewUnscoped"/>.</summary>
    EventView             = 1200,

    // ── API Key (1300–1399) ─────────────────────────────────────────────────

    /// <summary>View own API keys.</summary>
    ApiKeyView            = 1300,
    ApiKeyCreate          = 1301,
    ApiKeyEdit            = 1302,
    ApiKeyDelete          = 1303,
    /// <summary>View/manage every user's API keys (admin).</summary>
    ApiKeyViewAll         = 1310,
    ApiKeyDeleteAll       = 1311,

    // ── Identity Provider (1400–1499) ───────────────────────────────────────

    IdentityProviderView   = 1400,
    IdentityProviderCreate = 1401,
    IdentityProviderEdit   = 1402,
    IdentityProviderDelete = 1403,

    // ── AI Settings (1500–1599; Phase M11.A.6) ──────────────────────────────

    /// <summary>View this Space's AI provider + budget + MTD usage. API
    /// key is always returned masked in this scope.</summary>
    SpaceAiSettingsView    = 1500,
    /// <summary>Edit this Space's AI settings + reveal the API key + manage
    /// cost overrides. Reveal of the key writes a SpaceAi.ApiKeyRevealed
    /// audit row on every call.</summary>
    SpaceAiSettingsManage  = 1501,

    // ── Deployment Freezes (1600–1699; Phase M13.F.2) ───────────────────────

    /// <summary>View global deployment-freeze definitions.</summary>
    DeploymentFreezeView   = 1600,
    /// <summary>Create / edit / delete global deployment-freeze windows.</summary>
    DeploymentFreezeManage = 1601,
    /// <summary>
    /// Override an active freeze when starting a deployment. Granted to
    /// emergency-bypass roles only (a freeze without the option to override
    /// blocks security-patch hotfixes). Override use is always audited via
    /// <c>Freeze.Overridden</c> regardless of who clicked it.
    /// </summary>
    DeploymentFreezeOverride = 1602,

    // ── Reserved blocks for future entities ─────────────────────────────────
    // Add when the matching domain entity ships. Keeps the enum stable so
    // existing roles don't have their permission-set IDs renumbered.
    //   Worker:        2000–2099
    //   WorkerPool:    2100–2199
    //   Account:       2200–2299
    //   Certificate:   2300–2399
    //   Subscription:  2400–2499
}
