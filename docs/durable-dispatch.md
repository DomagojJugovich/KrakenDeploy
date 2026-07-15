# Durable Dispatch

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-07-15 |
| **Authors** | Domagoj Jugovic, Claude (Opus 4.8) |
| **Status** | Approved |
| **Technologies** | .NET 10, EF Core 10, PostgreSQL, Hangfire, Channels |
| **Projects** | KrakenDeploy.Server, KrakenDeploy.Server.Data, KrakenDeploy.Server.Transport, KrakenDeploy.Server.Core |

## Purpose

Covers the B1 engine-resilience batch (T0-1, T1-2): a server crash/restart must
never strand a deployment, and a deployment must execute exactly once. Before
B1: the dispatch queue was a non-durable in-process `Channel` (a crash between
the `Queued` insert and consumption stranded the row forever — nothing re-scans
`ScheduledFor == null` rows), a crash mid-run stranded it at `Running`, the
`Queued→Running` transition was a blind write (duplicate wake-ups double-ran
the plan), and a past-dated `ScheduledFor` was persisted *and* enqueued
(create-vs-job double dispatch). The minutely job even had its own stranding
bug: it cleared `ScheduledFor` *before* the channel writes.

## Durability posture (decided)

**The DB row is the source of truth; the in-process channels are only wake-up
signals.** Wake-up delivery is *at-least-once* (create-time enqueue, the
minutely job, the boot/sweep reconciler may all signal the same task);
execution is made *exactly-once* by the worker's **atomic claim**. The
alternative — routing immediate dispatch through Hangfire — was rejected:
Hangfire enqueues don't join the EF transaction (the reconciler is needed
anyway), Hangfire is itself at-least-once (the claim is needed anyway), and its
queue polling adds latency to every deployment.

## 1. Atomic claim + lease (`ServerTaskLease`)

`TryClaimAsync` is one conditional `UPDATE … WHERE status = Queued` that flips
the task to `Running`, stamps `StartedUtc`, `claimed_by` (forensic) and
`lease_until`, and clears `ScheduledFor` (a claimed task must never re-match
the scheduled job). Exactly one wake-up wins; a duplicate enqueue, a cancel
that landed during the worker's prep I/O, or a row already running elsewhere
all lose the `WHERE` and bail. Both workers claim — including the offline-drop
branch (a duplicate wake-up must not build/deliver the same bundle twice).

The **lease** is the ownership signal: `ServerTaskLeaseRenewal` renews it every
minute (5-minute TTL) for as long as the dispatch is in flight, each tick in
its own DI scope (the account context flows via `AsyncLocal`). Terminal writes
and the hand-off points (runbook run handed to the agent, offline drop parked
at `PendingOfflineResult`) release it. Ownership-by-lease — never by instance
name — is what keeps a **blue-green overlap** safe: the draining slot keeps
renewing, so the freshly booted slot's reconciler never touches its live runs.

> Tracked-entity subtlety: the claim is an `ExecuteUpdate`, which bypasses the
> change tracker. `MirrorClaim` syncs the entity **not-modified** (EF resets a
> property's current value to its original when `IsModified` is cleared, so
> originals are aligned first). A dirty mirror would let any later
> `SaveChanges` blindly re-assert `Running` over a concurrent `Cancelled`.

## 2. One dispatch path per deployment

`DeploymentService.CreateAsync` decides once: a due/past `scheduledFor` is
normalized to `null` and enqueued immediately; only a genuinely future instant
is persisted, and then the minutely job is the sole dispatcher. The job itself
(`ScheduledDeploymentDispatchJob`) is now a **pure, read-only signaller** — it
never mutates rows, so a crash mid-job strands nothing; the claim ends the
signalling.

## 3. Reconciler (boot + minutely sweep)

The same job body runs once at startup — before the workers begin consuming —
and every minute thereafter (per-account via the fan-out runner in
multi-account mode), so recovery never depends on a restart:

1. **Due scheduled deployments** → wake-up (unchanged behaviour).
2. **Stale `Queued` tasks** (both kinds, `ScheduledFor == null`, older than a
   2-minute grace): their create-time wake-up died with the process → re-signal
   to the right channel per `Kind`.
3. **Orphaned `Running` deployments** (lease expired *or never stamped*): the
   owning process is dead and its in-memory wave/sub-plan state is unresumable
   → conditional flip to `Failed` (the `UPDATE` re-checks status **and** lease,
   so a run whose owner renewed in between is left alone) + a
   **`Deployment.Interrupted`** audit row recording the claim owner and lease
   expiry — distinguishable from an ordinary failure when an operator asks why
   a deploy died at 03:00.

Never reconciled: rows with a **live lease**; **runbook runs** (after dispatch
they are agent-owned — `AgentHub` writes their terminal status even across a
server restart); **`PendingOfflineResult`** (parked awaiting an out-of-band
bundle).

## 4. Terminal-state guards

`DeploymentStatusExtensions.IsTerminal` is the single authority for the
terminal set `{Succeeded, SucceededWithWarnings, Failed, Cancelled}` (the
classification had already diverged across three inline copies). Every final
write now refuses to overwrite a terminal verdict:

- `DeploymentWorker` finalisation + `FailAsync` (previously cancel-only) — a
  zombie dispatch resumed after a lease-expiry reconcile cannot report success;
- `AgentHub.CompleteDeploymentAsync`'s fallback — a late agent callback cannot
  overwrite a cancel or a reconciler verdict with a stale success;
- `DeploymentService.CancelAsync` (unchanged semantics, shared helper).

## Failure-mode matrix

| Crash point | Before B1 | After B1 |
|---|---|---|
| After `Queued` insert, before channel consume | stranded `Queued` forever | boot/sweep re-signals within ≤1 min of startup/next tick |
| Mid-run (worker orchestrating) | stuck `Running` forever | lease expires ≤5 min → `Failed` + `Deployment.Interrupted` audit |
| Minutely job between clear and enqueue | stranded `Queued, ScheduledFor=null` forever | job is read-only; nothing to strand |
| Past-dated `scheduledFor` | dispatched twice | one path; claim de-duplicates any residue |
| Blue-green slot overlap | new slot could corrupt draining slot's runs (no reconciler existed, but a naive one would) | live lease ⇒ hands off |

## Residual gaps (documented, out of B1 scope)

- A runbook run that crashes **between** its `Running` claim and the hub send
  is stuck `Running` (indistinguishable from agent-owned execution without an
  agent-side contract). Post-B2 (agent reconnect), agent-side reporting is the
  recovery path.
- A `Running` deployment recovered as `Failed` is **not resumed** — wave
  progress and sub-plan state were in-memory. Operators redeploy; step
  idempotence is the deployment author's concern.
- The CLI's string-based terminal set (`ReleaseCommands.cs`) still omits
  `SucceededWithWarnings` — pre-existing adjacent defect, tracked separately.

## References

- `docs/production-readiness-audit-2026-07-13.md` — T0-1, T1-2.
- `docs/production-fix-prompts-2026-07-13.md` — B1 spec.
- [ExecuteUpdateAsync](https://learn.microsoft.com/ef/core/saving/execute-insert-update-delete)
- [PropertyEntry.IsModified](https://learn.microsoft.com/dotnet/api/microsoft.entityframeworkcore.changetracking.propertyentry.ismodified)
