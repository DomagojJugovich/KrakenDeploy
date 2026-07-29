# Agent Wire Contract — the B6 Freeze Pass

| | |
|---|---|
| **Version** | 1.2 |
| **Date** | 2026-07-29 |
| **Authors** | Domagoj Jugović, Claude (Opus 4.8), Claude (Opus 5) |
| **Status** | Approved |
| **Technologies** | .NET 10, SignalR, gRPC (proto3), PowerShell/Bash |
| **Projects** | KrakenDeploy.Contracts, KrakenDeploy.Agent, KrakenDeploy.Agent.Transport, KrakenDeploy.Server.Transport, KrakenDeploy.Steps.Common |

Production-readiness fix **B6** (audit items T2-5, T1-3): the last breaking pass
over the agent wire before external agents exist. Everything here is a
**CONTRACT CHANGE**; `AgentContract.CurrentVersion = 1` names the resulting
surface. Later passes append to it — the current version and what each pass
added are tabulated below. **The contract is at v3** (F5).

## Version history

| Version | Pass | Surface added / changed |
|---|---|---|
| 1 | B6 (this document) | `DispatchId` on plan + completion + step + log reports, `CancelDeploymentAsync` push, `AgentRegistrationResult`, `Roles` removed from registration. |
| 2 | F2 (2026-07-25) | `DeploymentPlan.AllowParallelTaskExecution` + `AdhocScriptCommand.AllowParallelTaskExecution` (per-target machine-concurrency policy, appended + defaulted `false`); new `IAgentHubServer.ReportExecutionStartedAsync(deploymentId, dispatchId)`. |
| 3 | F5 (2026-07-29) | **No shape change.** Both `AllowParallelTaskExecution` fields are RETAINED and re-interpreted: they now select which SIDE of the agent's reader-writer machine gate the work takes (`true` → SHARED, `false` → EXCLUSIVE) instead of whether to take it at all. `AdhocScriptCommand.AllowParallelTaskExecution` also changes provenance: per-RUN, not per-target — the AI session flow always sends `true`. |

**Why F2 bumps the version rather than riding v1.** Both new plan fields are
appended and default to the safe value, so a v1 agent would deserialize them
fine — but a v1 agent never sends `ReportExecutionStartedAsync`, and the server
now arms the wave deadline from it. Such an agent would leave every wave on the
dispatch-time backstop ceiling (`budget + Engine:MaxTargetQueueWait`, default
2 h) instead of its real budget, and would silently keep bypassing the machine
gate for ad-hoc scripts. That is a semantic divergence the refusal must catch,
not a wire-shape one.

**Why F5 bumps the version with no shape change at all.** This is the sharpest case
for versioning MEANING rather than structure. A v2 agent deserializes every field a v3
server sends and looks perfectly healthy — but it reads
`AllowParallelTaskExecution = true` as "skip the machine execution gate entirely", no
lock whatsoever, which is precisely the behaviour F5 removes. Two consequences make the
skew unacceptable rather than merely suboptimal: the AI ad-hoc flow now sends `true`
unconditionally, so on a v2 agent **every** approved diagnostic script would run
ungated, straight into a running deployment's file / IIS / service operations; and the
server would believe the gate had been honoured. Nothing on the wire distinguishes the
two readings, so the registration refusal is the only place it can be caught.
Pinned by `AgentHubRegisterTests.RegisterAsync_refuses_the_previous_contract_version`.

The rule this establishes: bump on a change to how the agent must INTERPRET an existing
field, not only on a change to the shapes.

## The contract, versioned

`AgentRegistrationRequest` now carries `ContractVersion`, and `RegisterAsync`
returns an `AgentRegistrationResult`. A mismatch is **refused loudly**: the
server removes the connection from its dispatch registry (undispatchable
immediately), marks the target Offline without the 30 s flicker grace, records
an `Agent.ContractVersionRejected` audit row per attempt (deliberate
visibility) and tells the agent why. The agent logs the upgrade instruction and
retries on the 5-minute slow lane — it self-heals after the binary upgrade.

This replaces the pre-B6 failure mode: when `stepIndex` was added to
`AppendLogAsync` with no negotiation, an old agent stayed connected and every
report it sent was **silently dropped** by signature mismatch.

`Roles` is **removed** from the registration request (T1-7's end state):
authorization roles are operator-assigned server-side; self-declaration is now
unrepresentable on the wire, which supersedes the old ignore-and-audit path.

## Cooperative cancel (T2-5)

`IAgentHubClient.CancelDeploymentAsync(taskId, reason)` — taskId covers both
deployments and runbook runs (the agent knows both as
`DeploymentPlan.DeploymentId`).

- **Server side**: `DeploymentService.CancelAsync` / `RunbookService.
  CancelRunAsync` record the Cancelled verdict FIRST (B5 guarded write), then
  fire `IAgentCancelPusher` best-effort — a missed push (offline agent)
  degrades to the wave-boundary stop, never to a lost cancel. Runbook runs got
  their first cancel surface: service method, `POST
  /api/runbook-runs/{id}/cancel`, a per-row Cancel button on the runbook
  detail page (TaskCancel permission throughout).
- **Agent side**: `DeploymentExecutor` keeps a single-flight registry
  (`taskId → (DispatchId, CancellationTokenSource)`). The cancel signals the
  run's token — which already flowed through `StepRetryRunner` into every
  `IStepHandler.HandleAsync(ctx, ct)` — and the executor reports a failed
  `"Aborted on the agent"` completion for the attempt. The server's terminal
  guard swallows it (operator cancel) or the stale DispatchId drops it
  (supersede).
- **Process-tree kill**: `ScriptRunner` (shared by deployment script steps AND
  adhoc scripts) now kills the child's whole process tree on cancellation and
  reaps it with a 10 s guard. Pre-B6, `WaitForExitAsync(ct)` only stopped
  waiting — every cancel and per-step timeout leaked an orphan process tree.

## Attempt idempotency, completed (T1-3)

B2 introduced the per-attempt `DispatchId` (plan + completion + step reports +
registry slots keyed per attempt; retries regenerate the id). B6 finishes it:

- `AppendLogAsync` now echoes it — the server drops lines whose attempt the
  registry has **positively retired** (a superseded/timed-out attempt's outbox
  still flushing), so an abandoned attempt cannot interleave noise into the
  current attempt's log. `Guid.Empty` and unknown ids (runbook hand-offs,
  post-restart) are always accepted.
- The agent enforces **single-flight per task**: a re-delivered copy of the
  same attempt is ignored (the original reports once); a NEWER attempt
  supersedes — the old attempt is cancelled and awaited (30 s unwind guard)
  before the new one starts, so two attempts never touch the same extract
  dirs / IIS handles concurrently.

## Trust-boundary hardening

- `ReportStepCompletedAsync` rejects a negative `stepIndex` at the hub, and the
  worker's wave fold range-guards every agent-supplied index against the plan
  snapshot before indexing — `int.MaxValue` from a buggy/malicious agent used
  to throw inside the fold and abort the whole cross-target deployment.
- `AppendLogAsync` clamps `stepIndex < -1` to the plan-level sentinel.

## resume_offset removed from kraken.proto

`DownloadRequest.resume_offset` (field 4, now `reserved`): no agent ever set
it, and a resumed partial range could not carry the full-file SHA-256 — the
proto explicitly suppressed the hash on resumed transfers, making resumption an
**unverified download path**. Interrupted transfers restart; delta transfer
(`base_version`) already minimizes the cost for agents with a cached base.
Full transfers now always emit the integrity hash.

## Reserved for post-v1 cert auth

`AgentEnrollmentContracts.cs` freezes the enrollment/PoP wire shapes from
`docs/design-agent-enrollment-cert-auth.md` as unserved definitions: the
enroll route + request/response records, the connect-nonce route, and the
DPoP-style proof header name. The real forward-compat lever is
`AgentContract.CurrentVersion` — shipping PoP bumps it, and pre-PoP agents get
the explicit refusal instead of a silent protocol mismatch.

## Residuals

- **Online-until-registered window**: `OnConnectedAsync` marks the target
  Online (and dispatchable) before `RegisterAsync` runs, so a version-refused
  agent is briefly selectable for dispatch (sub-second). A dispatch landing in
  that window behaves like pre-B6 (reports dropped); the wave deadline (B3)
  reaps it.
- **Runbook claim→hand-off race**: a cancel landing between the B1 claim and
  the agent hand-off pushes to an agent that doesn't have the task yet (no-op);
  the run then executes fully on the agent and its completion is swallowed by
  the terminal guard — the pre-B6 semantics, only for that window.
- **New agent vs old server**: `RegisterAsync` deserializes a void reply as a
  null result and proceeds; upgrade servers first (standing rule for this
  fleet).
- Server-side script steps (`ServerScriptStepRunner`) still leak the child
  process on timeout — B7.

## Tests

`DeploymentExecutorCancelTests` (gate link holds the first completion in
flight: cancel / duplicate / supersede / unknown), `ScriptRunnerKillTests`
(REAL PowerShell process publishes its PID; cancellation must terminate it),
`AgentHubRegisterTests` (contract refusal: result + registry removal + Offline
+ audit), `AgentHubOwnershipTests` (retired-dispatch log drop; negative
stepIndex reject), `OrchestratorCancellationTests` (cancel pushes to connected
agents, skips offline ones), `RunbookRunCancelTests` (flip + push, terminal
refusal, unknown id).

## References

- `docs/production-fix-prompts-2026-07-13.md` — B6 work package
- `docs/agent-reconnect.md` (B2), `docs/status-concurrency.md` (B5)
- `docs/design-agent-enrollment-cert-auth.md` — the reserved shapes' design
