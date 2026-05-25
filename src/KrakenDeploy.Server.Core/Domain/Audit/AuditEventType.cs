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

    // ── Performance settings (M13.F.3) ───────────────────────────────────────
    /// <summary>Operator changed instance-wide performance / retention knobs
    /// on <c>/configuration/performance</c>. Details: each changed field
    /// with old → new value, comma-separated. Operators reviewing the
    /// audit log can see a clear chronology of "who tuned what when".</summary>
    public const string PerformanceSettingsUpdated = "Performance.SettingsUpdated";

    /// <summary>A deployment exceeded the configured
    /// <c>SlowDeploymentThresholdMinutes</c> window. Emitted at deployment
    /// finalization. Subscribable via M13.B.2/3 — operators route to
    /// webhook / email / runbook / AI inspection. Details: deployment id +
    /// duration in minutes + threshold.</summary>
    public const string DeploymentSlow             = "Deployment.Slow";

    /// <summary>A single step exceeded the configured
    /// <c>SlowStepThresholdMinutes</c> window. v1 fires for server-side
    /// steps only; target-side per-step timing needs an agent contract
    /// change for per-step boundaries. Details: deployment id + step name +
    /// duration in minutes + threshold.</summary>
    public const string DeploymentStepSlow         = "DeploymentStep.Slow";

    // ── M14 step-execution events ────────────────────────────────────────────
    /// <summary>A step was skipped because its Run Condition didn't match
    /// the current deployment state (e.g. Success-conditioned step after
    /// a prior failure, Failure-conditioned step in a clean deployment).
    /// Details: deployment id, step name, condition, reason.</summary>
    public const string DeploymentStepSkipped         = "Deployment.StepSkipped";

    /// <summary>A step was killed because it exceeded its configured
    /// <c>TimeoutSeconds</c>. Treated as a step failure subject to the
    /// step's <c>Required</c> flag (Required → deployment aborts;
    /// not-Required → loop continues with hasFailed=true).
    /// Details: deployment id, step name, timeout seconds, elapsed.</summary>
    public const string DeploymentStepTimedOut        = "Deployment.StepTimedOut";

    /// <summary>A step was retried after a failure. Emitted per retry
    /// attempt. Details: deployment id, step name, attempt number,
    /// max retries, retry delay seconds.</summary>
    public const string DeploymentStepRetried        = "Deployment.StepRetried";

    /// <summary>A Required step failed (after retries, if any).
    /// Deployment aborts. Details: deployment id, step name, reason.</summary>
    public const string DeploymentRequiredStepFailed = "Deployment.RequiredStepFailed";

    /// <summary>A non-Required step failed; the deployment continues
    /// but is marked as having non-required failures (terminal status
    /// becomes <c>SucceededWithWarnings</c>). Details: deployment id,
    /// step name, reason.</summary>
    public const string DeploymentStepFailedNonRequired = "Deployment.StepFailedNonRequired";

    /// <summary>The Variable-Condition expression on a step failed to
    /// resolve (referenced an undefined variable or returned an unexpected
    /// non-boolean shape). The step is skipped (falsy fallback) and the
    /// audit row carries the expression for forensic review.
    /// Details: deployment id, step name, expression.</summary>
    public const string DeploymentVariableConditionUnresolved = "Deployment.VariableConditionUnresolved";

    // ── Maintenance mode (M13.A.3) ───────────────────────────────────────────
    /// <summary>Operator enabled instance-wide maintenance mode.
    /// Details: reason text + who enabled it. Pair with
    /// <see cref="MaintenanceDisabled"/> so audit readers see the window
    /// as a bracketed [Enabled ... Disabled] interval.</summary>
    public const string MaintenanceEnabled         = "Maintenance.Enabled";
    /// <summary>Operator disabled maintenance mode.</summary>
    public const string MaintenanceDisabled        = "Maintenance.Disabled";

    // ── Subscriptions (M13.B.2/3) ────────────────────────────────────────────
    /// <summary>Operator created an EventSubscription.</summary>
    public const string SubscriptionCreated        = "Subscription.Created";
    /// <summary>Operator modified an existing EventSubscription.</summary>
    public const string SubscriptionUpdated        = "Subscription.Updated";
    /// <summary>Operator deleted an EventSubscription.</summary>
    public const string SubscriptionDeleted        = "Subscription.Deleted";
    /// <summary>A transport delivery for a matched event completed
    /// successfully. Details: subscription id + transport + detail blurb
    /// + elapsed ms.</summary>
    public const string SubscriptionDeliverySucceeded = "Subscription.DeliverySucceeded";
    /// <summary>A transport delivery failed (after Hangfire exhausted its
    /// retry policy). Details: subscription id + transport + error
    /// message + final attempt number.</summary>
    public const string SubscriptionDeliveryFailed = "Subscription.DeliveryFailed";

    /// <summary>The AI inspection transport produced a diagnosis. Written
    /// as its own audit event so the diagnosis itself becomes subscribable
    /// (the "diagnose, then post to Slack" workflow chains two subscriptions).
    /// Details: subject event id + truncated summary.</summary>
    public const string DiagnosisCompleted         = "Diagnosis.Completed";

    // ── Backup (M13.G) ───────────────────────────────────────────────────────
    /// <summary>Operator changed the backup schedule / target directory /
    /// retention. Details: enabled + cron + target + retention.</summary>
    public const string BackupSettingsUpdated   = "Backup.SettingsUpdated";
    /// <summary>One backup run finished successfully (manual or scheduled).
    /// Details: bundle path + size + duration + triggered-by.</summary>
    public const string BackupCompleted         = "Backup.Completed";
    /// <summary>One backup run failed. Details: triggered-by + error message
    /// (verbatim from BackupEngine — operator-actionable).</summary>
    public const string BackupFailed            = "Backup.Failed";

    // ── Deployment Freezes (M13.F.2) ─────────────────────────────────────────
    /// <summary>Operator created a new freeze definition. Details: name +
    /// window + scope summary.</summary>
    public const string FreezeCreated           = "Freeze.Created";
    /// <summary>Operator updated a freeze definition. EF interceptor writes
    /// the field-level before/after via the entity-modified path; this
    /// constant is kept for explicit code paths to use if they want a
    /// freeze-specific event type instead of the generic Modified one.</summary>
    public const string FreezeUpdated           = "Freeze.Updated";
    /// <summary>Operator deleted a freeze definition.</summary>
    public const string FreezeDeleted           = "Freeze.Deleted";
    /// <summary>DeploymentWorker blocked a deployment because an active freeze
    /// matched its scope. Details: freeze id + name + end-time.</summary>
    public const string DeploymentBlockedByFreeze = "Deployment.BlockedByFreeze";

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
