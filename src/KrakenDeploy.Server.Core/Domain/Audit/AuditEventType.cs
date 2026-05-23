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

    // ── Licensing (M13.E.1) ──────────────────────────────────────────────────
    /// <summary>Operator uploaded a license key on /settings/license. Details
    /// carry the sanitised summary (customer + type + expiry + caps) — never
    /// the raw JWT, which is sensitive vendor-signed material.</summary>
    public const string LicenseUploaded         = "License.Uploaded";
    /// <summary>Upload attempt rejected (invalid signature, expired, malformed).
    /// Recorded so forensic review can spot license-key brute-force attempts.</summary>
    public const string LicenseUploadRejected   = "License.UploadRejected";

    // ── Features panel (M13.F.1) ──────────────────────────────────────────────
    /// <summary>Operator toggled a per-instance feature flag on
    /// <c>/configuration/features</c>. Details carry the key + new state +
    /// whether the new state matches the catalogue default (so audit
    /// readers can spot deviations at a glance).</summary>
    public const string FeatureFlagUpdated      = "Feature.Updated";

    // ── SMTP (M13.B.1) ───────────────────────────────────────────────────────
    /// <summary>Operator updated the server-wide SMTP settings. Details carry
    /// the host + port + TLS mode + from-address + whether the password was
    /// changed — never the password itself.</summary>
    public const string SmtpSettingsUpdated     = "Smtp.SettingsUpdated";
    /// <summary>Operator clicked "Send test email" and the probe succeeded.
    /// Details: recipient + elapsed time.</summary>
    public const string SmtpTestProbeSucceeded  = "Smtp.TestProbeSucceeded";
    /// <summary>Operator clicked "Send test email" and the probe failed.
    /// Details: recipient + MailKit error message (no credentials leaked).</summary>
    public const string SmtpTestProbeFailed     = "Smtp.TestProbeFailed";
}
