namespace KrakenDeploy.Contracts;

/// <summary>
/// The agent wire-contract version this assembly speaks. Sent by the agent in
/// <see cref="AgentRegistrationRequest.ContractVersion"/> and enforced by the
/// server at registration: a mismatch is REFUSED with an explicit
/// <see cref="AgentRegistrationResult"/> instead of the pre-B6 failure mode
/// (silently dropped log/step reports after an unnegotiated signature change).
/// Bump on every breaking change to the SignalR agent surface
/// (<see cref="IAgentHubServer"/> / <see cref="IAgentHubClient"/>) or to
/// <see cref="DeploymentPlan"/> — including a change to how the agent must
/// INTERPRET an existing field, not only to the shapes themselves (see <c>3</c>).
/// </summary>
public static class AgentContract
{
    /// <summary>
    /// Version history:
    /// <list type="bullet">
    ///   <item><c>1</c> — the B6 freeze surface: DispatchId on plan + completion +
    ///     step + log reports, CancelDeploymentAsync push, registration result,
    ///     Roles removed from registration.</item>
    ///   <item><c>2</c> — F2: <see cref="DeploymentPlan.AllowParallelTaskExecution"/>
    ///     and <c>AdhocScriptCommand.AllowParallelTaskExecution</c> on the wire, plus
    ///     the new <see cref="IAgentHubServer.ReportExecutionStartedAsync"/> report
    ///     (the server arms the wave deadline from it, so a v1 agent would leave
    ///     every wave on the dispatch-time backstop).</item>
    ///   <item><c>3</c> — F5: no shape change, a MEANING change. Both
    ///     <c>AllowParallelTaskExecution</c> fields are retained but now select which
    ///     SIDE of the agent's reader-writer machine gate the work takes
    ///     (<c>true</c> → SHARED, <c>false</c> → EXCLUSIVE) instead of whether to
    ///     take it at all. A v2 agent reads <c>true</c> as a full bypass — no lock
    ///     whatsoever — which is precisely the behaviour F5 removes, so the skew is
    ///     invisible on the wire and MUST be refused at registration rather than
    ///     negotiated. The ad-hoc dispatch path also changed which value it sends:
    ///     the AI-session flow is now read-always. Also adds
    ///     <c>GET /api/agents/task-in-flight</c>, which the agent MUST consult
    ///     (fail-closed) immediately before a self-upgrade swap: the machine gate is
    ///     released at every wave boundary, so a gate-only check cannot see that a
    ///     multi-wave deployment is still mid-flight.</item>
    /// </list>
    /// </summary>
    public const int CurrentVersion = 3;
}

/// <summary>
/// Sent by the agent immediately after the SignalR connection is established,
/// providing full machine information so the server can populate the target record.
/// <para>
/// B6 CONTRACT CHANGE: <c>Roles</c> is REMOVED (T1-7 — roles are authorization,
/// operator-assigned server-side, and were already ignored + audited when
/// self-declared; the field no longer exists on the wire).
/// <see cref="ContractVersion"/> is ADDED — a pre-B6 agent deserializes to the
/// default 0 and is refused with a clear upgrade message.
/// </para>
/// </summary>
public sealed record AgentRegistrationRequest(
    Guid TargetId,
    string MachineName,
    string OperatingSystem,
    string AgentVersion,
    long FreeDiskBytes,
    long TotalRamBytes,
    int ContractVersion);

/// <summary>
/// B6 — the server's verdict on a registration. <c>Accepted == false</c> means
/// the agent must NOT expect to receive work (the server has removed the
/// connection from its dispatch registry); the agent logs
/// <see cref="Message"/>, drops the connection and retries on its slow lane so
/// it self-heals after an agent upgrade. Pre-B6 agents invoked
/// <c>RegisterAsync</c> as void and simply ignore this payload — their refusal
/// is enforced server-side.
/// </summary>
public sealed record AgentRegistrationResult(
    bool Accepted,
    int ServerContractVersion,
    string? Message = null);

/// <summary>
/// Sent every 30 s by the agent. Only non-null fields are applied on the server.
/// </summary>
public sealed record HeartbeatRequest(
    string? MachineName,
    string? OperatingSystem,
    string? AgentVersion,
    long? FreeDiskBytes);

/// <summary>
/// Returned by GET /api/agents/update-info. Tells the agent whether a newer
/// version is available and where to download it.
/// <para>
/// C6: <see cref="Sha256"/> is now MANDATORY whenever <see cref="UpdateAvailable"/>
/// is true — the server computes it from the actual binary
/// (<c>ServerAgentUpdateService.ComputeSha256</c>) rather than echoing the
/// manifest's defaultable field, and an agent refuses an update that arrives
/// without one. <see cref="ServerContractVersion"/> is the wire-contract version
/// THIS server speaks (<see cref="AgentContract.CurrentVersion"/>);
/// <see cref="TargetContractVersion"/> is the version the offered build speaks
/// (from the manifest). The agent refuses to apply an update whose target
/// contract version does not match the server's (self-inflicted-skew guard) and
/// reports the refusal.
/// </para>
/// </summary>
public sealed record AgentUpdateInfo(
    bool UpdateAvailable,
    string? LatestVersion,
    string? DownloadUrl,
    long? SizeBytes,
    string? Sha256,
    int ServerContractVersion = 0,
    int? TargetContractVersion = null);

/// <summary>
/// C6 — body for POST /api/agents/update-status. The agent reports the outcome
/// of a self-upgrade attempt so it is visible server-side (audit trail on the
/// target). <see cref="Outcome"/> is one of the
/// <see cref="AgentUpdateOutcome"/> string constants. Never carries binaries or
/// secrets — only versions and a short human-readable <see cref="Detail"/>.
/// </summary>
public sealed record AgentUpdateStatusReport(
    string Outcome,
    string? FromVersion,
    string? ToVersion,
    string? Detail);

/// <summary>
/// F5 — response of <c>GET /api/agents/task-in-flight</c>. Answers the one question
/// the agent cannot answer locally: does the SERVER still have a non-terminal task
/// assigned to this target?
/// <para>
/// The agent's machine execution gate is released and re-taken at every WAVE
/// boundary (the server dispatches per wave), so between two waves of a live
/// multi-wave deployment the gate is free and the in-flight registry is empty. A
/// self-upgrade that trusted only local state would pass its checks there, swap the
/// binaries and <c>Environment.Exit</c>, killing the deployment mid-plan — and the
/// window is not small, because a SERVER wave (a manual intervention, a
/// <c>DeployRelease</c> cascade) can sit between two target waves for minutes or
/// hours. Only the server sees whole plans, so only the server can answer.
/// </para>
/// <para>
/// The target id comes from the agent JWT, never a parameter, so an agent can only
/// ask about itself. Consumed fail-closed: an unreachable or unparseable answer
/// means "assume in flight" and defer the swap.
/// </para>
/// </summary>
/// <param name="InFlight">
/// <c>true</c> when at least one non-terminal <c>ServerTask</c> (deployment or
/// runbook run, of any wave) is assigned to this target. <c>Queued</c> counts:
/// a task that has not been claimed yet can be dispatched here at any moment, and
/// a swap started now would race its first wave.
/// <para>
/// NULLABLE on purpose. As a non-nullable <c>bool</c> on a positional record, an
/// absent <c>inFlight</c> property bound to <c>default</c> — so any HTTP 200 whose
/// body did not come from this server (a reverse proxy or auth gateway returning
/// <c>{}</c> or a JSON error envelope) deserialized to "idle" and the agent's
/// fail-CLOSED check silently failed OPEN, swapping mid-plan. <c>null</c> now means
/// "no answer", which the agent treats as in-flight.
/// </para>
/// </param>
/// <param name="Detail">
/// Short, non-sensitive description for the agent's log (e.g. a task count). Never
/// carries project, environment, tenant or variable data — an agent is not
/// authorized to learn about work it has not been dispatched.
/// </param>
public sealed record AgentTaskInFlightResponse(bool? InFlight, string? Detail);

/// <summary>
/// C6 — the discrete outcomes an agent reports for a self-upgrade attempt.
/// String constants (not an enum) so the wire form is unambiguous regardless of
/// the server's JSON enum-serialisation settings.
/// </summary>
public static class AgentUpdateOutcome
{
    /// <summary>The new version booted healthy within the probation window; the
    /// backup was discarded and the upgrade is committed.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>The new version failed the post-restart health gate; the agent
    /// restored the previous version from backup.</summary>
    public const string RolledBack = "rolled-back";

    /// <summary>An update was refused before any swap because the server did not
    /// supply a mandatory SHA-256 hash.</summary>
    public const string HashMissing = "hash-missing";

    /// <summary>An update was refused before any swap because the archive's
    /// SHA-256 did not match the server-supplied hash.</summary>
    public const string HashMismatch = "hash-mismatch";

    /// <summary>An update was refused before any swap because the offered build's
    /// wire-contract version does not match the server's.</summary>
    public const string ContractSkew = "contract-skew";

    /// <summary>The swap itself failed and was rolled back in-process (the agent
    /// keeps running the previous binary).</summary>
    public const string SwapFailed = "swap-failed";

    /// <summary>
    /// F5 — the swap was DEFERRED (not failed): the machine stayed busy for the whole
    /// swap window, so nothing was touched and the next check will retry. Reported so
    /// a machine that keeps deferring is visible server-side; a gate held by a wedged
    /// step is otherwise indistinguishable from a healthy busy agent, and the agent's
    /// own logs are local-only.
    /// </summary>
    public const string SwapDeferred = "swap-deferred";
}

/// <summary>Body for POST /api/deployments/{id}/logs.</summary>
public sealed record DeploymentLogLineRequest(string Level, string Message);

/// <summary>Body for POST /api/deployments/{id}/complete.</summary>
public sealed record CompleteDeploymentRequest(bool Success, string? ErrorMessage);
