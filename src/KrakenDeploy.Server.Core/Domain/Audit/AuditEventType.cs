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

    /// <summary>A per-user API key was minted (M13.C.4). Details: key name +
    /// prefix hint + owner + expiry + Space restriction — never the token.
    /// Per-use rows are deliberately NOT written; <c>api_keys.last_used_utc</c>
    /// covers usage without flooding the audit table.</summary>
    public const string ApiKeyCreated     = "Security.ApiKeyCreated";
    /// <summary>A per-user API key was revoked. Details: key name + prefix
    /// hint + owner. Revocation is immediate — the auth handler re-reads the
    /// row on every request.</summary>
    public const string ApiKeyRevoked     = "Security.ApiKeyRevoked";

    /// <summary>The key-encryption key (KEK / <c>Encryption:MasterKey</c>) was
    /// rotated: the DEK was re-wrapped under a new KEK, no data re-encrypted
    /// (M13.D.2). Details: never the key material — just that it happened.</summary>
    public const string EncryptionKekRotated = "Security.EncryptionKekRotated";
    /// <summary>The data-encryption key (DEK) was rotated: a new DEK was
    /// generated and every secret re-encrypted under it in one transaction
    /// (M13.D.2). Details: per-store re-encryption counts, never plaintext.</summary>
    public const string EncryptionDekRotated = "Security.EncryptionDekRotated";

    /// <summary>An offline-drop target's per-target HMAC signing key was
    /// (re)generated. Rotation invalidates in-flight bundles. Details: target id.</summary>
    public const string OfflineDropHmacKeyGenerated   = "OfflineDrop.HmacKeyGenerated";
    /// <summary>An offline-drop target's per-target AES bundle key was
    /// (re)generated and the raw key disclosed once to the caller. Rotation
    /// makes existing bundles undecryptable. Details: target id.</summary>
    public const string OfflineDropBundleKeyGenerated = "OfflineDrop.BundleKeyGenerated";
    /// <summary>An offline-drop deployment's drop bundle was regenerated (from
    /// the UI/API) while it awaited its offline result. The bundle is a pure
    /// function of the frozen release snapshot, so this re-materialises an
    /// equivalent deployable; <c>plan.enc</c> is re-encrypted with a fresh
    /// nonce. Details: deployment id.</summary>
    public const string DropBundleRegenerated = "OfflineDrop.BundleRegenerated";

    // ── Deployment lifecycle (non-entity events) ──────────────────────────────
    public const string DeploymentStarted   = "Deployment.Started";
    public const string DeploymentSucceeded = "Deployment.Succeeded";
    public const string DeploymentFailed    = "Deployment.Failed";
    public const string DeploymentCancelled = "Deployment.Cancelled";

    /// <summary>B1 — the dispatch reconciler failed a <c>Running</c> deployment
    /// whose lease had expired: the process orchestrating it died (crash,
    /// restart) and its in-memory wave/sub-plan state cannot be resumed. The
    /// terminal status is <c>Failed</c>; this event is what distinguishes
    /// "interrupted by a dead server" from an ordinary step failure when an
    /// operator asks why a deploy died at 03:00. Details: claim owner + lease
    /// expiry.</summary>
    public const string DeploymentInterrupted = "Deployment.Interrupted";

    // ── Runbook lifecycle ─────────────────────────────────────────────────────
    public const string RunbookRunStarted   = "RunbookRun.Started";
    public const string RunbookRunSucceeded = "RunbookRun.Succeeded";
    public const string RunbookRunFailed    = "RunbookRun.Failed";

    /// <summary>B3 — the dispatch reconciler failed a <c>Running</c> runbook run
    /// whose lease had EXPIRED: the dispatching process died between the atomic
    /// claim and the agent hand-off, so the plan never reached the agent.
    /// Runbook analogue of <see cref="DeploymentInterrupted"/>.</summary>
    public const string RunbookRunInterrupted = "RunbookRun.Interrupted";

    /// <summary>B3 — the dispatch reconciler failed an agent-owned <c>Running</c>
    /// runbook run (lease released at hand-off) whose <c>StartedUtc</c> exceeded
    /// <c>Engine:MaxRunbookRunDuration</c>: the agent never reported completion
    /// and nothing else can ever finalize the run. A late completion after this
    /// reap is swallowed by the hub's terminal-status guard.</summary>
    public const string RunbookRunTimedOut = "RunbookRun.TimedOut";

    /// <summary>B6 — an operator cancelled a runbook run (runbook analogue of
    /// <see cref="DeploymentCancelled"/>; same TaskCancel permission).</summary>
    public const string RunbookRunCancelled = "RunbookRun.Cancelled";

    /// <summary>B6 — an agent registered with a wire-contract version this
    /// server does not speak. The registration was refused, the connection
    /// removed from the dispatch registry and the target marked Offline; the
    /// agent binary must be upgraded. Fires on every refused registration —
    /// a persistently outdated agent produces a row per reconnect, which is
    /// deliberate visibility, not noise.</summary>
    public const string AgentContractVersionRejected = "Agent.ContractVersionRejected";

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

    /// <summary>Two or more parallel siblings in the same wave (M14.4 Start
    /// Trigger = StartWithPrevious) wrote to the same output variable name.
    /// The winning step is the last in SortOrder; the audit row records
    /// every (losing step, value) plus the winner so an operator can
    /// trace which step's value was used for downstream
    /// <c>Octopus.Action[Step].Output.X</c> references and which was lost.
    /// Storage stays per-step (no DB collision), so the audit is purely
    /// informational. Details: deployment id, wave step names, variable
    /// name, winning step, losing steps.</summary>
    public const string DeploymentParallelOutputCollision = "Deployment.ParallelOutputCollision";

    /// <summary>A deployment process contained a wave (M14.4 Start Trigger
    /// chain) mixing server-side and target-side steps. v1 refuses this
    /// at orchestrator pre-flight — dispatching a parallel sub-plan to
    /// the agent while also running server-side steps creates a 4-way
    /// cancellation tree without a compelling use case. Operators split
    /// such mixed waves into two single-side parallel groups run
    /// sequentially. Details: deployment id, wave step names, server
    /// step names, target step names.</summary>
    public const string DeploymentMixedWaveRefused = "Deployment.MixedWaveRefused";

    /// <summary>M15 — a ForEach-mode Step Group's collection variable
    /// resolved to an empty array, so the loop body emitted zero plans.
    /// Operators see this as a no-op step group in the Steps tab; the
    /// audit row preserves the variable name + step name for forensic
    /// review. Details: deployment id, step group name, collection
    /// variable name.</summary>
    public const string DeploymentForEachEmpty = "Deployment.ForEachEmpty";

    /// <summary>M15 — a ForEach-mode Step Group's collection variable
    /// could not be resolved (referenced an undefined array variable).
    /// The flattener emits a synthetic failing plan so the Required gate
    /// decides whether the missing collection aborts the deployment.
    /// Details: deployment id, step group name, collection expression.</summary>
    public const string DeploymentForEachUnresolved = "Deployment.ForEachUnresolved";

    /// <summary>M-RollingDeployments Phase 2 — emitted when a target wave's
    /// fan-out is batched by the rolling window
    /// (<c>Octopus.Action.MaxParallelism</c> on a <c>Kraken.StepGroup</c>
    /// ancestor). One audit per batch start so operators can trace which
    /// subset of targets received this slice of the dispatch.
    /// Details: deployment id, rolling group name, batch index (1-based),
    /// total batches, batch target names, wave step names.</summary>
    public const string DeploymentRollingBatchStarted = "Deployment.RollingBatchStarted";

    /// <summary>M-RollingDeployments Phase 2 — emitted when a rolling-batch
    /// dispatch settles (success OR failure). Pair with
    /// <see cref="DeploymentRollingBatchStarted"/> for "open + close"
    /// timeline reconstruction. Required failures inside the batch ALSO
    /// trip <see cref="DeploymentRequiredStepFailed"/>; this row just
    /// records the batch's terminal state.
    /// Details: deployment id, rolling group name, batch index, success
    /// flag, failed target names (if any).</summary>
    public const string DeploymentRollingBatchCompleted = "Deployment.RollingBatchCompleted";

    /// <summary>M-RollingDeployments Phase 3 — a target drops out of
    /// subsequent waves because its current wave hit a Required step
    /// failure OR the agent dropped offline. Other alive targets keep
    /// running; only when ALL targets have dropped does the deployment
    /// transition to <see cref="DeploymentStatus.Failed"/>. When SOME
    /// targets dropped but others succeeded, the deployment terminates
    /// as <see cref="DeploymentStatus.SucceededWithWarnings"/> — the
    /// audit row preserves which target dropped and why so partial
    /// success is forensically reviewable.
    /// Details: deployment id, target name, target id, drop reason
    /// (Required step name or "offline"), wave step names, error message.</summary>
    public const string DeploymentTargetDropped = "Deployment.TargetDropped";

    /// <summary>M-RollingDeployments Phase 3 — per-target dimension of
    /// <see cref="DeploymentSlow"/>. Emitted at deployment finalisation
    /// for every target whose effective duration
    /// (max <c>CompletedUtc</c> − min <c>StartedUtc</c> across its
    /// <see cref="Deployments.TaskStepOutcome"/> rows) exceeded the
    /// <c>SlowDeploymentThresholdMinutes</c> window. Lets operators
    /// pinpoint which specific machine slowed a multi-target run, even
    /// if the deployment as a whole stayed under threshold (when only
    /// one target straggled).
    /// Details: deployment id, target id, target name, duration in
    /// minutes, threshold in minutes.</summary>
    public const string DeploymentTargetSlow = "Deployment.TargetSlow";

    // ── MCP server (M11.B) ───────────────────────────────────────────────────
    /// <summary>M11.B — an MCP client read a <c>kraken://</c> resource.
    /// One row per resource read so the forensic trail shows which AI
    /// client (via the API key's principal) pulled which deployment /
    /// process / config content. Subject is the resource URI.
    /// Details: resource URI + a short outcome note (ok / not-found).</summary>
    public const string McpResourceRead = "Mcp.ResourceRead";

    /// <summary>M11.B — an MCP client invoked a tool. One row per tool
    /// call. Mutating tools (e.g. retry_deployment) ALSO write their
    /// domain audit event; this row captures the MCP entry point.
    /// Subject is the tool name. Details: tool name + arguments summary
    /// + a short outcome note.</summary>
    public const string McpToolInvoked = "Mcp.ToolInvoked";

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

    // ── Ad-hoc agent actions (M11.E) ─────────────────────────────────────────

    /// <summary>The static-analysis gate rejected an ad-hoc script (the
    /// generated iter 1, an operator-edited approval, or a verdict's proposed
    /// fix). Details: iter number + violation summary.</summary>
    public const string AdhocGateRejected      = "Adhoc.GateRejected";
    /// <summary>Operator approved an iteration's signed script for execution.
    /// Details: iter number + approver display.</summary>
    public const string AdhocIterationApproved = "Adhoc.IterationApproved";
    /// <summary>Operator rejected an iteration's proposed script. The
    /// session stays Active; the operator can re-prompt or stop.</summary>
    public const string AdhocIterationRejected = "Adhoc.IterationRejected";
    /// <summary>Session closed — either via "Mark resolved" (M11.E.16) or
    /// because the verdict was AllSucceeded / NoFixAvailable or the proposed
    /// fix failed the gate. Details: reason.</summary>
    public const string AdhocSessionClosed     = "Adhoc.SessionClosed";
    /// <summary>Operator pressed "Stop session" (M11.E.16). Details: who.</summary>
    public const string AdhocSessionStopped    = "Adhoc.SessionStopped";
    /// <summary>Session auto-closed after hitting its iteration cap
    /// (M11.E.14). Details: cap + "manual intervention required".</summary>
    public const string AdhocSessionCapReached = "Adhoc.SessionCapReached";

    // ── Agent registration (T1-7) ────────────────────────────────────────────

    /// <summary>A registering agent supplied a non-empty authorization Roles
    /// list. Roles are operator-assigned only (they drive secret scoping), so
    /// the value is IGNORED and this event is recorded — it signals a tampered
    /// or outdated agent. Details: target id + the rejected roles.</summary>
    public const string AgentRoleSelfAssignmentRejected = "Agent.RoleSelfAssignmentRejected";

    /// <summary>A8/T1-12 — an operator revoked a target's agent bearer token(s)
    /// by bumping its token version. Any outstanding token is rejected on next
    /// connect/call and the live tunnel (if any) is dropped immediately; the
    /// agent must re-enroll. Details: target id + new token version.</summary>
    public const string AgentTokenRevoked = "Agent.TokenRevoked";

    /// <summary>A8 — an agent renewed its bearer token via the sliding-refresh
    /// endpoint, authenticated by its current (non-revoked) token. One row per
    /// refresh gives a forensic trail of which agent renewed when — an anomaly
    /// spike here can indicate a stolen token being kept alive. Details: none
    /// beyond the subject (never token content).</summary>
    public const string AgentTokenRefreshed = "Agent.TokenRefreshed";

    // ── Agent self-upgrade (C6) ────────────────────────────────────────────────

    /// <summary>C6 — an agent self-upgrade booted healthy within the probation
    /// window and was committed (previous version discarded). Reported by the
    /// agent to POST /api/agents/update-status. Details: from/to versions.</summary>
    public const string AgentUpdateApplied = "Agent.UpdateApplied";

    /// <summary>C6 — an agent self-upgrade failed its post-restart health gate and
    /// the agent automatically restored the previous version from backup. A row
    /// here means a published build is bad on that target — investigate before
    /// re-publishing. Details: from/to versions + reason.</summary>
    public const string AgentUpdateRolledBack = "Agent.UpdateRolledBack";

    /// <summary>C6 — an agent refused or aborted a self-upgrade BEFORE committing
    /// it (missing/mismatched hash, contract skew, or an in-process swap failure
    /// that left the previous binary running). Details: outcome + versions.</summary>
    public const string AgentUpdateFailed = "Agent.UpdateFailed";
}
