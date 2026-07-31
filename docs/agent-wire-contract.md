# Agent Wire Contract — the B6 Freeze Pass

| | |
|---|---|
| **Version** | 1.7 |
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
Pinned by `AgentHubRegisterTests.RegisterAsync_does_not_re_check_the_contract_version` (the hub trusts the gate) plus `MultiAccountAgentTransportE2ETests.Agent_with_a_skewed_contract_version_is_refused` (the gate refuses it).

The rule this establishes: bump on a change to how the agent must INTERPRET an existing
field, not only on a change to the shapes.

> **OPERATOR ACTION on every bump — the update manifest must be bumped with it.**
> Nothing in the repo declares a build's contract version: the only source is the
> operator-authored `version.json` behind `AgentRidInfo.ContractVersion`, which
> `ServerAgentUpdateService` serves as `TargetContractVersion`. A v4 server refuses every
> v3 agent on the handshake (intended), but if the manifest still says `3` the agent's own
> `EvaluateOffer` returns `ContractSkew` and refuses to apply the upgrade — so the fleet
> cannot self-heal out of the refusal, and every target stays Offline until an operator
> fixes the manifest by hand. Bump `version.json` in the same change as
> `AgentContract.CurrentVersion`.
>
> Once the manifest is right, recovery does NOT wait for the maintenance window. A refused
> agent sets `AgentContext.ContractRefused`, and that bypasses both the window and the
> connected check for the swap — see "Escaping a refusal" below.

### Escaping a refusal

A 426 refusal is a **deadlock unless the swap can happen while disconnected**, and getting
that wrong is what made the first cut of the handshake move unshippable. The self-upgrade
swap required `IServerLink.IsConnected`, but a 426 throws out of `StartAsync`, so the state
is permanently `Disconnected`: `update-info` still answered, the archive downloaded and
hash-verified on every tick, and the swap was then skipped with a `LogDebug` — below the
shipped `MinimumLevel: Information`, so invisible. Bumping the contract on a fleet meant a
manual reinstall on every target.

What the agent can see, and it is not much: `HttpConnection.NegotiateAsync` calls
`EnsureSuccessStatusCode()` before reading the response, so the gate's body **and** its
`X-KD-Contract-Server` header are discarded — the agent's exception message is only
"Response status code does not indicate success: 426 (Upgrade Required)". The status code
survives on `HttpRequestException.StatusCode`, and that is enough to classify. The server
log line is the only place both version numbers appear together.

So `ContractRefused` bypasses exactly two swap preconditions, and each has a reason it does
not apply:

| Precondition | Why it exists | Why it does not apply to a refused agent |
|---|---|---|
| `IsConnected` | a swap must not strand an agent mid-conversation | there is no conversation to strand, and there never will be until the binary changes |
| Inside the maintenance window | a restart must not disrupt work | no work can be dispatched to a refused agent, and honouring the window leaves it dark until 02:00–04:00 local — up to ~22 h after a server upgrade |

Everything that actually protects running work is still enforced: `DeploymentExecutor.IsExecuting`
(a deployment that started before the server was upgraded keeps running locally), the
server-side `GET /api/agents/task-in-flight` probe — which answers over REST, is
contract-agnostic, and fails **closed** on any unclear answer — and the machine execution
gate's EXCLUSIVE side, which waits out the ad-hoc scripts the in-flight check cannot see.
The refusal also takes the 5-minute operator-action lane rather than the 30 s exponential
one, and its log line names an **agent binary upgrade** rather than re-enrollment (the
401/403 remedy).

## Where the version is checked (F5)

**On the HANDSHAKE, before the connection is admitted.** The agent sends its version in the
`X-KD-Contract` request header (`AgentContract.VersionHeader`), which rides both the
negotiate request and the WebSocket upgrade, and persists across automatic reconnects — the
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
the audit row cannot carry the agent's BUILD version — only the contract version and the
target identity. That is enough to act on ("upgrade the agent on this target"), which is why
it is accepted rather than fixed. The earlier justification for accepting it was wrong and is
recorded here so it is not repeated: the build version is NOT unobtainable from
`Agent.Transport` — `Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()`
yields the same string `MachineInfoCollector` reports, and `ServerLinkHostedService` already
holds the value. The cost is one more handshake header, and the reason not to spend it is that
the target identity already tells an operator which box to touch.

### Reconnect pacing: which loop paces which failure

Three rounds got this wrong in both directions, so the facts below are pinned by execution
against a real hub in `ReconnectE2ETests`, not by reading framework source.

| Failure | What the SignalR client does | Who paces it |
|---|---|---|
| Transport drop of an established link (network blip, server restart, slot swap) | `Reconnecting` fires, automatic reconnect engages, `NextRetryDelay` is consulted — and it is consulted BEFORE `Reconnecting` is raised | `AgentReconnectPolicy`, inside the connection |
| Handshake refusal (426 from the contract gate, 401 from a revoked token) | `StartAsync` throws | `ServerLinkHostedService`'s supervision loop, connect lane |
| Rejection from inside the hub — unknown target, retired target, missing claim, or a throw from a saturated tenant DB | `StartAsync` **succeeds** (the handshake completes before `OnConnectedAsync` runs), then **`Closed` fires**. `Reconnecting` never fires and the retry policy is never consulted — for a throw and for `Context.Abort()` alike | `ServerLinkHostedService`'s supervision loop, post-park lane |

The third row is the one that has been mis-analysed repeatedly. Round 3 put a delay after the
supervisor's park; round 4 deleted it on the premise that `Closed` never fires for a
server-side rejection, and moved the pacing into a policy "churn lane" fed from
`Reconnecting`. Both halves were wrong: `Closed` does fire, and `Reconnecting` does not — so
the churn lane could not observe the failure it existed for, and the free-running park meant
`StartAsync` succeeded again immediately, reset the counter, and repeated at round-trip
cadence from every agent at once against a server already failing. The churn lane is deleted.

What the supervision loop counts is **cycles that never produced an accepted registration**.
An accepted registration — not a successful connect — clears it, which is what
`RegistrationOutcome.Accepted` has always documented. A healthy link that closes after a
normal server restart therefore still reconnects immediately, while a cycle the server
rejects escalates through the shared jittered curve to the 30 s cap.


## What a refusal does, and what it does not

A skew is **refused loudly**, and every part of that is on the handshake path:

- **426 Upgrade Required**, before the connection is admitted.
- The target is marked **Offline** and the change is **pushed to the UI**. Without this the
  whole fleet reads Online after a contract-bumping upgrade until `AgentLastSeenOfflineJob`
  catches it — a 3-minute threshold on a 5-minute cron, so up to ~8 minutes — and that job
  does not call the status publisher, so an open dashboard stays green until reload. An
  operator mid-upgrade reading a green fleet concludes it went fine.
- An `Agent.ContractVersionRejected` audit row naming the target, attributed to `System`.
  Reporting is throttled per (target, presented value) per 10 minutes: a refusal is a per-target
  STATE, not an event stream, and the subscription poller forwards audit rows off-premises to
  the webhook and e-mail transports. The 426 itself is never throttled.
- The response is written **before** any of the above. The recording half needs a resolved
  tenant database, and with Npgsql's `EnableRetryOnFailure` a slow one can take seconds — put
  that on the negotiate's critical path and a struggling database turns a clean, diagnosable 426
  into a client-side timeout.

**What the refusal cannot carry: the agent's BUILD version.** See "One thing the move costs"
above. After an agent ROLLBACK the targets list therefore keeps advertising the newer version —
the one field an operator uses to decide what to upgrade. Accepted residual.

This whole mechanism replaces the pre-B6 failure mode: when `stepIndex` was added to
`AppendLogAsync` with no negotiation, an old agent stayed connected and every report it sent was
**silently dropped** by signature mismatch.

`Roles` is **removed** from the registration request (T1-7's end state): authorization roles are
operator-assigned server-side; self-declaration is now unrepresentable on the wire, which
supersedes the old ignore-and-audit path.

**The one thing to watch.** Enforcement rides a request HEADER, so a header-whitelisting
intermediary would strip it and the gate would admit every agent silently — taking the fleet
dark later with a message blaming the wrong cause. The precedent cited for header safety
(`X-KD-Release`) does not transfer: that header is OPTIONAL, so its working has never proved the
path preserves headers. `AgentRegistrationRequest.ContractVersion` is still on the wire and
`RegisterAsync` compares the two, logging an Error naming the header when they disagree. That
comparison is the only thing that can detect a stripping proxy.

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

- ~~**Online-until-registered window**~~ — CLOSED by the move to the handshake. A
  version-refused agent never reaches `OnConnectedAsync`, so it is never marked Online and
  never selectable for dispatch, not even sub-second. The window this described no longer
  exists.
- **Agent BUILD version after a rollback**: a refused connection sends no registration payload
  and the build version is not on the handshake, so `DeploymentTarget.AgentVersion` keeps
  advertising the version the agent rolled back FROM. Closing it costs one more handshake
  header; the target identity already tells an operator which box to touch.
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

The contract gate, in the order a failure would be caught:

| Suite | What it pins |
|---|---|
| `AgentContractHandshakeGateTests` | The middleware's own contract: 426 for skewed / absent / garbled / duplicated / signed values, the metadata scoping (an endpoint without the marker is never inspected), the body and its content type, that the warning always lands, the report throttle and its (target, value) key, the truncated echo, and that an already-aborted request still produces a complete refusal. |
| `AgentContractRefusalRecorderTests` | The tenant-DB half, against real Postgres: Online → Offline plus the status push, that Disabled (retired) is not downgraded and Offline is not rewritten, and the persisted audit row — `subjectName`, and `UserId` null / `UserDisplay` "System" with a live agent principal on the `HttpContext`, which is the only way to catch the attribution fallback. |
| `MultiAccountAgentTransportE2ETests` | A real SignalR client: both skewed shapes refused with an asserted **426** (not merely "some failure" — a 401 routes the agent to the wrong lane), the registry never touched, and that the gate is unreachable without an agent credential (`/hubs/agent/negotiate` → 401, `/hubs/agent/x` → 404). |
| `TransportRoundTripTests` | Real loopback Kestrel over a real WebSocket, with the gate on both hub endpoints — so a header that failed to survive the upgrade would refuse every test in the suite. The transport is ASSERTED (via the `Upgrade` request header) rather than assumed, so a silent fallback to SSE or long polling turns the suite red instead of quietly weakening it. |
| `AgentHubRegisterTests` | That the hub does NOT re-check the version; the header-strip tripwire; that a failed write leaves no dispatchable registry entry; and that the cancel re-push survives a failed machine-info write. |
| `AgentReconnectPolicyTests`, `ServerLinkHostedServiceTests`, `ReconnectE2ETests` | The agent side: 426 takes the operator-action lane with the binary-upgrade remedy, the escape hatch opens for 426 only, unproductive cycles are paced, and the SignalR behaviours the whole pacing design rests on. |

Neighbouring suites unchanged by this pass: `DeploymentExecutorCancelTests`,
`ScriptRunnerKillTests`, `AgentHubOwnershipTests`, `OrchestratorCancellationTests`,
`RunbookRunCancelTests`.

## Document history

Distinct from the wire-contract version table above — these are revisions of this
DOCUMENT, and conflating the two axes is exactly the trap the operator callout warns
about.

| Version | Date | Change |
|---|---|---|
| 1.7 | 2026-07-31 | F5 round 5 REVIEW fixes. Retracts this document's own "the agent hub is not on WebSockets" residual, which was **wrong**: it came from reading `HttpContext.WebSockets.IsWebSocketRequest` in a middleware mounted before the endpoint, where SignalR has not yet installed `IHttpWebSocketFeature`, so a genuine upgrade read false. Re-measured via the `Upgrade` REQUEST header: the agent negotiates `upgrade: websocket` and carries `X-KD-Contract` on it, so v1.6's "correction" of the earlier commits was itself the error and their wording is restored. Also: the 426 now calls `Response.CompleteAsync()` (writing the body alone left the negotiate blocked for the whole recording — 3056 ms vs 1 ms measured — which turned a diagnosable refusal into a client timeout), and the refusal-report throttle is DELETED because it gated the Offline mark and status push, which are reconciled state rather than events. |
| 1.6 | 2026-07-31 | F5 review round 5. Contract bumped **3 → 4**: the header move is its own version, because "requires v3, presented absent" while both sides call themselves v3 reads as a server fault, and because a non-change to `CurrentVersion` never fired the OPERATOR ACTION rule. Rewrote the reconnect-pacing section against MEASURED SignalR behaviour — a rejection inside `OnConnectedAsync` fires `Closed`, never `Reconnecting`, so round 4's "churn lane" could not observe the failure it existed for and is deleted; pacing is back in the supervision loop, keyed on an accepted registration. Added "Escaping a refusal" (the 426 deadlock and the two swap preconditions `ContractRefused` bypasses). Replaced "The contract, versioned", which described the deleted in-hub refusal, with what a refusal actually does now — including the Offline mark and status push it had silently lost, the report throttle, and the response-before-recording order. Corrected the reason `AgentVersion` is unobtainable (it is obtainable; the cost is one header) and moved it to Residuals. Struck the "Online-until-registered window" residual, which the move closed. Replaced the Tests paragraph with a table keyed on what each suite pins. |
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
