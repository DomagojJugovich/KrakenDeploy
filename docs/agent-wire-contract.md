# Agent Wire Contract — the B6 Freeze Pass

| | |
|---|---|
| **Version** | 1.5 |
| **Date** | 2026-07-31 |
| **Authors** | Domagoj Jugović, Claude (Opus 4.8), Claude (Opus 5) |
| **Status** | Approved |
| **Technologies** | .NET 10, SignalR, gRPC (proto3), PowerShell/Bash |
| **Projects** | KrakenDeploy.Contracts, KrakenDeploy.Agent, KrakenDeploy.Agent.Transport, KrakenDeploy.Server.Transport, KrakenDeploy.Steps.Common |

Production-readiness fix **B6** (audit items T2-5, T1-3): the last breaking pass
over the agent wire before external agents exist. Everything here is a
**CONTRACT CHANGE**; `AgentContract.CurrentVersion = 1` names the resulting
surface. Later passes append to it — the current version and what each pass
added are tabulated below. **The contract is at v4** (F5).

## Version history

| Version | Pass | Surface added / changed |
|---|---|---|
| 1 | B6 (this document) | `DispatchId` on plan + completion + step + log reports, `CancelDeploymentAsync` push, `AgentRegistrationResult`, `Roles` removed from registration. |
| 2 | F2 (2026-07-25) | `DeploymentPlan.AllowParallelTaskExecution` + `AdhocScriptCommand.AllowParallelTaskExecution` (per-target machine-concurrency policy, appended + defaulted `false`); new `IAgentHubServer.ReportExecutionStartedAsync(deploymentId, dispatchId)`. |
| 3 | F5 (2026-07-29) | **No shape change on the SignalR surface.** Both `AllowParallelTaskExecution` fields are RETAINED and re-interpreted: they now select which SIDE of the agent's reader-writer machine gate the work takes (`true` → SHARED, `false` → EXCLUSIVE) instead of whether to take it at all. `AdhocScriptCommand.AllowParallelTaskExecution` also changes provenance: per-RUN, not per-target — the AI session flow always sends `true`. Adds one REST endpoint the agent MUST consult fail-closed before a self-upgrade swap: `GET /api/agents/task-in-flight` → `AgentTaskInFlightResponse`. Adds the `swap-deferred` `AgentUpdateOutcome`. |
| 4 | F5 round 5 (2026-07-31) | **The version itself MOVED onto the handshake.** The agent sends `X-KD-Contract` on the negotiate and the WebSocket upgrade; the server refuses a mismatch with **426** before the connection is admitted. `AgentRegistrationRequest.ContractVersion` is retained for diagnostics and is no longer a gate. Round 4 folded this into v3 on the grounds that v3 had never shipped; that made the refusal incoherent to read — an agent built against the pre-move v3 sends no header and was refused with "requires v3, presented absent" while both sides call themselves v3. A distinct number makes the diagnosis self-explanatory and is what fires the OPERATOR ACTION rule below. |

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

> **OPERATOR ACTION on every bump — the update manifest must be bumped with it.**
> Nothing in the repo declares a build's contract version: the only source is the
> operator-authored `version.json` behind `AgentRidInfo.ContractVersion`, which
> `ServerAgentUpdateService` serves as `TargetContractVersion`. A v3 server refuses every
> v2 agent at registration (intended), but if the manifest still says `2` the agent's own
> `EvaluateOffer` returns `ContractSkew` and refuses to apply the upgrade — so the fleet
> cannot self-heal out of the refusal, and every target stays Offline until an operator
> fixes the manifest by hand. Recovery then still waits for the maintenance window.
> Bump `version.json` in the same change as `AgentContract.CurrentVersion`.

## Where the version is checked (F5)

**On the HANDSHAKE, before the connection is admitted.** The agent sends its version in the
`X-KD-Contract` request header (`AgentContract.VersionHeader`), which rides both the
negotiate request and the WebSocket upgrade and persists across automatic reconnects — the
same mechanism the blue-green release pin `X-KD-Release` already relies on.
`AgentContractHandshakeGate`, mounted after authentication so its audit row can name the
target, compares it and answers **426 Upgrade Required** on any mismatch. Absent and
unparseable are refused too: an agent old enough to predate the header is precisely the case
that must not be read as compatible.

Past that gate, **connected == verified == dispatchable**. `IAgentConnectionRegistry` has one
predicate for it, `GetConnectionId`, and `HasConnectionFor` answers the same question rather
than a narrower one.

The check previously lived in `RegisterAsync`, a hub METHOD, and that single ordering choice
generated a family of defects across three review rounds. Because a hub method cannot run
until the connection exists, the server had to admit a connection whose version it did not
yet know — so it needed a second predicate ("has completed registration") to keep work away
from it, which then had to be explained separately to the offline grace and to B3's mid-wave
disconnect monitor (gating LIVENESS on registration let the monitor diagnose "agent
disconnected" against an agent that was still executing, and under `Atomic` failure mode that
triggers farm-wide cleanup). A registration that threw left the connection permanently
undispatchable, so the hub aborted it to force a retry the agent would not ask for — and that
abort was itself harmful twice over: `Context.Abort()` drops the transport rather than closing
it, so the client's automatic reconnect retried it at round-trip cadence forever, and removing
the registry entry alongside it suppressed the target's offline mark entirely.

None of that machinery exists now. `RegisterAsync` records the machine's self-reported details
and re-pushes cooperative cancels for tasks that went terminal while the agent was away; both
are best-effort, and a throw means only that this cycle did not record machine info. Pinned by
`AgentContractHandshakeGateTests` (the middleware's own contract, including 426 and the audit
row) and `MultiAccountAgentTransportE2ETests.Agent_with_a_skewed_contract_version_is_refused`
(a real SignalR client, both the skewed and absent shapes).

**One thing the move costs.** The refused connection never sends a registration payload, so
the audit row can no longer carry the agent's BUILD version — only the contract version and
the target identity. That is enough to act on ("upgrade the agent on this target"), and
`SignalRServerLink` lives in a different assembly from the version it would have to report, so
adding a second header was not worth the churn.

**Reconnect pacing lives in `AgentReconnectPolicy`, not in the supervision loop.**
`RetryContext.PreviousRetryCount` restarts at zero for every reconnect episode and attempt
zero is deliberately immediate, so a connection that keeps dying moments after it is
established never backs off. The policy therefore also counts consecutive SHORT-LIVED
connections and paces on whichever count is higher; one connection that lasts
`MinUsefulConnection` clears it. This is what bounds the remaining `OnConnectedAsync` aborts
(deleted target, retired target, missing claim), which are otherwise the same unpaced loop.


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
+ audit; and F5's "a v2 agent is refused"), `AgentConnectionRegistryReconnectTests`
(F5 dispatch eligibility: connected-but-unregistered is not dispatchable while
liveness stays true; a removed connection cannot be marked; re-adding starts
unregistered), `AgentHubOwnershipTests` (retired-dispatch log drop; negative
stepIndex reject), `OrchestratorCancellationTests` (cancel pushes to connected
agents, skips offline ones), `RunbookRunCancelTests` (flip + push, terminal
refusal, unknown id).

## Document history

Distinct from the wire-contract version table above — these are revisions of this
DOCUMENT, and conflating the two axes is exactly the trap the operator callout warns
about.

| Version | Date | Change |
|---|---|---|
| 1.5 | 2026-07-31 | F5 review round 4: the wire-contract check moved from `RegisterAsync` onto the SignalR handshake (`X-KD-Contract` → 426), which deletes the connected-but-unverified state and with it `MarkRegistered`, `IsRegistered` and the liveness-vs-eligibility split. Rewrote "Dispatch eligibility" as "Where the version is checked", recorded the one thing the move costs (the audit row can no longer name the agent BUILD version), and documented that reconnect pacing lives in `AgentReconnectPolicy`'s churn lane rather than the supervision loop — round 3 had put it on a path `Context.Abort()` never wakes. |
| 1.4 | 2026-07-30 | F5 review round 3: corrected "Dispatch eligibility" — `HasConnectionFor` answers LIVENESS and is NOT gated on registration (the earlier text described a behaviour that was reverted), and documented the two constraints on the registration-failure abort (agent-side backoff paced on ACCEPTED registration; the abort must not remove the registry entry). |
| 1.3 | 2026-07-29 | F5 review follow-up: amended the v3 row (the REST additions it also covers), added the OPERATOR ACTION callout about bumping `version.json` in the same change, and added the "Dispatch eligibility (F5)" section. |
| 1.2 | 2026-07-29 | F5: added the v3 contract row and the "why a meaning change bumps the version" rationale. |
| 1.1 | 2026-07-25 | F2: added the v2 contract row. |
| 1.0 | — | Initial B6 freeze pass. |

## References

- `docs/production-fix-prompts-2026-07-13.md` — B6 work package
- `docs/agent-reconnect.md` (B2), `docs/status-concurrency.md` (B5)
- `docs/design-agent-enrollment-cert-auth.md` — the reserved shapes' design
