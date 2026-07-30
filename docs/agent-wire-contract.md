# Agent Wire Contract — the B6 Freeze Pass

| | |
|---|---|
| **Version** | 1.4 |
| **Date** | 2026-07-30 |
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
| 3 | F5 (2026-07-29) | **No shape change on the SignalR surface.** Both `AllowParallelTaskExecution` fields are RETAINED and re-interpreted: they now select which SIDE of the agent's reader-writer machine gate the work takes (`true` → SHARED, `false` → EXCLUSIVE) instead of whether to take it at all. `AdhocScriptCommand.AllowParallelTaskExecution` also changes provenance: per-RUN, not per-target — the AI session flow always sends `true`. Adds one REST endpoint the agent MUST consult fail-closed before a self-upgrade swap: `GET /api/agents/task-in-flight` → `AgentTaskInFlightResponse`. Adds the `swap-deferred` `AgentUpdateOutcome`. |

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

## Dispatch eligibility (F5)

`OnConnectedAsync` has to register a connection before the agent can invoke anything, so
"connected" and "contract-verified" are different states — and dispatch keys on the
second. `IAgentConnectionRegistry.MarkRegistered` is called only after `RegisterAsync`
passes, and **`GetConnectionId` ignores anything unmarked**.

Before F5 the registry entry alone made a connection dispatchable, so a skewed agent
could be handed work in the connect→register window — and *permanently* if its
`RegisterAsync` invoke threw, because that failure is swallowed as retryable and only
re-sent on the next reconnect. Combined with the AI ad-hoc path now sending `true`
unconditionally, a v2 agent in that state would run **every** approved script with no
machine gate at all while the server believed the gate was honoured. Gating the lookup
fixes every dispatch consumer at once rather than each remembering to ask.

**`HasConnectionFor` is deliberately NOT gated on registration.** It answers LIVENESS
("did the agent reconnect, is it still there"), and its consumers are the hub's 30 s
offline grace and B3's mid-wave disconnect monitor. Answering those with dispatch
eligibility flips a healthy target Offline during the connect→register window and, worse,
lets the disconnect monitor cancel a wave still executing on a connected agent — which
under `Atomic` failure mode triggers farm-wide cleanup. The two predicates are different
questions on purpose, and any other `IAgentConnectionRegistry` implementation (a
Redis-backed one for multi-node scale-out, say) must preserve the split in both
directions. Pinned by `AgentConnectionRegistryReconnectTests`, which asserts liveness
`true` and eligibility `false` for the same connection.

Because an unmarked connection is undispatchable and invisible, `RegisterAsync` aborts it
when `RegisterCoreAsync` throws, forcing the reconnect the agent's own retry classifier
will not ask for. Two constraints on that abort, each of which was violated first:

- The agent **paces** those cycles. Only an ACCEPTED registration resets its supervision
  backoff, because a cycle that connects cleanly and then fails registration is still a
  failed cycle. Resetting on a successful `StartAsync` instead pinned the retry count at 0
  — where the policy's delay is deliberately `TimeSpan.Zero` — so a server aborting
  registration (an unhealthy tenant DB, say) got reconnected at RTT cadence by every agent
  in the fleet, against the database that was already failing.
- The abort must **not** remove the registry entry. `OnDisconnectedAsync` gates all of its
  bookkeeping on winning that same removal, so doing it in the catch silently suppressed
  the target's offline mark: the row kept the `Online` status `OnConnectedAsync` wrote,
  with a fresh `LastSeenUtc` on every loop. It also stripped the `_registered` entry, which
  cancelled out the reason `MarkRegistered` runs before the reconnect reconcile.

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
| 1.4 | 2026-07-30 | F5 review round 3: corrected "Dispatch eligibility" — `HasConnectionFor` answers LIVENESS and is NOT gated on registration (the earlier text described a behaviour that was reverted), and documented the two constraints on the registration-failure abort (agent-side backoff paced on ACCEPTED registration; the abort must not remove the registry entry). |
| 1.3 | 2026-07-29 | F5 review follow-up: amended the v3 row (the REST additions it also covers), added the OPERATOR ACTION callout about bumping `version.json` in the same change, and added the "Dispatch eligibility (F5)" section. |
| 1.2 | 2026-07-29 | F5: added the v3 contract row and the "why a meaning change bumps the version" rationale. |
| 1.1 | 2026-07-25 | F2: added the v2 contract row. |
| 1.0 | — | Initial B6 freeze pass. |

## References

- `docs/production-fix-prompts-2026-07-13.md` — B6 work package
- `docs/agent-reconnect.md` (B2), `docs/status-concurrency.md` (B5)
- `docs/design-agent-enrollment-cert-auth.md` — the reserved shapes' design
