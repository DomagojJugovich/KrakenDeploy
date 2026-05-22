namespace KrakenDeploy.Server.Core.Domain.Audit;

/// <summary>
/// Well-known audit event type string constants.
/// EF entity lifecycle events ("Project.Created" etc.) are constructed
/// dynamically by <c>AuditLogInterceptor</c> — only non-EF events need
/// explicit constants here.
/// </summary>
public static class AuditEventType
{
    // ── Authentication ────────────────────────────────────────────────────────
    public const string UserSignedIn      = "User.SignedIn";
    public const string UserSignedOut     = "User.SignedOut";
    public const string UserOidcSignedIn  = "User.OidcSignedIn";
    public const string UserInvited       = "User.Invited";
    public const string UserDeleted       = "User.Deleted";
    public const string UserLoginFailed   = "User.LoginFailed";
    public const string UserLockedOut     = "User.LockedOut";

    // ── Security ──────────────────────────────────────────────────────────────
    public const string PermissionDenied  = "Security.PermissionDenied";
    public const string ApiKeyUsed        = "Security.ApiKeyUsed";

    // ── Deployment lifecycle (non-entity events) ──────────────────────────────
    public const string DeploymentStarted   = "Deployment.Started";
    public const string DeploymentSucceeded = "Deployment.Succeeded";
    public const string DeploymentFailed    = "Deployment.Failed";
    public const string DeploymentCancelled = "Deployment.Cancelled";

    // ── Runbook lifecycle ─────────────────────────────────────────────────────
    public const string RunbookRunStarted   = "RunbookRun.Started";
    public const string RunbookRunSucceeded = "RunbookRun.Succeeded";
    public const string RunbookRunFailed    = "RunbookRun.Failed";

    // ── Step package lifecycle (Phase D) ─────────────────────────────────────
    public const string StepPackageInstalled    = "StepPackage.Installed";
    public const string StepPackageUninstalled  = "StepPackage.Uninstalled";
    public const string StepPackageBulkUpgraded = "StepPackage.BulkUpgraded";

    // ── AI settings (M11.A.6) ────────────────────────────────────────────────
    /// <summary>Operator updated this Space's AI settings via PUT.</summary>
    public const string SpaceAiSettingsUpdated  = "SpaceAi.SettingsUpdated";
    /// <summary>Operator viewed the decrypted API key via the reveal endpoint.
    /// Sensitive operation — every call writes a row regardless of outcome.</summary>
    public const string SpaceAiApiKeyRevealed   = "SpaceAi.ApiKeyRevealed";
}
