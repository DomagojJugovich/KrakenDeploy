# ServerTask Status Concurrency (B5)

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-07-16 |
| **Authors** | Domagoj Jugović, Claude (Opus 4.8) |
| **Status** | Approved |
| **Technologies** | .NET 10, EF Core 10, Npgsql 10, PostgreSQL |
| **Projects** | KrakenDeploy.Server.Data, KrakenDeploy.Server.Transport |

Production-readiness fix **B5** (audit items T1-1, T1-5): optimistic concurrency
on the `server_tasks` spine plus a single guarded write path for every status
transition, so no writer can silently overwrite another writer's terminal
verdict.

## The problem

Every status writer was a guarded read-check-write: re-read the status, bail if
terminal, then `SaveChanges`. The guard itself was correct (B1 added it to the
hub fallback, finalize, `FailAsync`, `CancelAsync`), but not atomic — a verdict
landing **between the check and the save** was silently overwritten:

- cancel vs. late agent completion → `Cancelled` flipped back to `Succeeded`,
  and retention/compaction fired for a task finalized elsewhere;
- cancel vs. worker finalize / `FailAsync` → same lost update in either
  direction;
- `RunbookRunWorker.FailAsync` had **no guard at all** — a run cancelled after
  the claim, or already reaped by the B3 reconciler, was overwritten with
  `Failed` and a fresh `CompletedUtc`;
- the offline paths had long windows: `IngestAsync` verifies and extracts a
  whole bundle between its status check and its write, so a result upload could
  resurrect a deployment cancelled mid-ingest.

## The fix

### 1. xmin as the concurrency token

`ServerTaskConfiguration` maps Postgres's `xmin` system column as the EF
row-version token (`Property<uint>("xmin").HasColumnName("xmin").IsRowVersion()`
— Npgsql 10 dropped the old `UseXminAsConcurrencyToken()` helper). Every
tracked UPDATE of a `server_tasks` row now carries `WHERE xmin = <original>`;
a lost update surfaces as `DbUpdateConcurrencyException` instead of silently
winning. The migration is model-only — xmin exists on every Postgres row, so
there is no DDL (the scaffolded `AddColumn` was removed per Npgsql guidance).

### 2. `ServerTaskStatusWriter` — the single write path

`TryTransitionAsync(db, task, apply, canTransitionFrom?)`:

1. reload authoritative values (fresh token **and** fresh status) via
   `GetDatabaseValues`;
2. evaluate the guard — default `!status.IsTerminal()`, offline sites use
   `status == PendingOfflineResult`; refused → `false`, nothing written, and
   the tracked entity holds the authoritative status;
3. `apply(task)`, `SaveChanges`;
4. on `DbUpdateConcurrencyException`: retry from 1 (bounded, 5 attempts);
5. row no longer exists (retention pruned it) → `false`.

**Reload-first is required, not defensive.** Two untracked writers update the
row constantly while a task runs — the log-sequence allocation
(`TaskLogService`, raw UPDATE per staged batch) and the B1 lease renewal
(every minute). Any long-lived tracked entity therefore carries a stale token
almost immediately; saving it directly would conflict on nearly every write
(the offline transition is the extreme case: the B1 claim itself bumps xmin
right after the entity is loaded). Reloading inside the write window shrinks
the race surface to microseconds, and the bounded retry absorbs the rare
interleaved bump — e.g. a cancel racing a chatty deployment's log appends
retries instead of failing the operator's action.

**Why `SaveChanges`, not a conditional `ExecuteUpdate`.** A status-guarded
`UPDATE … WHERE status NOT IN (…)` would be atomic too, but it bypasses the
change-tracker pipeline: the `AuditLogInterceptor`'s `Deployment.Updated`
rows and `ModifiedUtc` stamping would be silently lost, and callers rely on
staged child rows committing atomically with the status flip (offline ingest).

### 3. Writers routed through it

| Writer | Guard | Notes |
|---|---|---|
| `AgentHub.CompleteDeploymentAsync` fallback | terminal | compaction/UI/retention only when the write won |
| `DeploymentWorker` finalize | terminal | `didSucceed` = transition won → retention gating unchanged |
| `DeploymentWorker.FailAsync` | terminal | AI-diagnosis gate fires only when the Failed write won |
| `DeploymentWorker.DispatchOfflineDropAsync` | terminal | replaces the cancelled-only pre-check; refuses reconciler-failed rows too; orphaned bundle logged |
| `RunbookRunWorker.FailAsync` | terminal | **was blind — the T1-5 bug** |
| `DeploymentService.CancelAsync` | terminal | same already-terminal `InvalidOperationException` contract, now off the authoritative status |
| `OfflineResultService.IngestAsync` | `== PendingOfflineResult` | verdict computed early, written last; children commit atomically with it; refusal rejects the upload with nothing persisted |
| `OfflineDropBundleBuilder` regenerate | `== PendingOfflineResult` | defensive `DropBundlePath` backfill |

Writers that were already atomic stay as conditional `ExecuteUpdate`s and are
**not** token-checked (by design — the WHERE clause is their guard): the B1
claim/renew/release (`ServerTaskLease`), the dispatch reconciler's orphan/reap
steps, and the log-sequence allocation.

### 4. Audit interceptor hardening (found in review)

A failed save left the `AuditLogInterceptor`'s staged `AuditEntry` rows
tracked, so any catch-and-continue caller persisted them with its next save —
the writer's retry produced two `Deployment.Updated` rows for one transition,
the first describing a save that never happened. The interceptor now detaches
its per-save cohort on failure, hooked at **both** `SaveChangesFailed` and
`ThrowingConcurrencyException` — concurrency conflicts surface through their
own interception point, and the failed-save hook alone demonstrably did not
cover the retry path. `xmin` is also excluded from audit snapshots.

## Semantics to remember

- A refused transition returns `false`; the tracked entity then holds the
  fresh DB values, so `task.Status` is the verdict that blocked it.
- The helper's `SaveChanges` flushes the caller's whole context — staged
  children ride along atomically (intended; the offline ingest depends on it).
- Retry exhaustion (5 consecutive microsecond-window conflicts) rethrows —
  treat it as a fault, not contention.
- `PendingOfflineResult` stays **non-terminal** (`IsTerminal()` is the single
  authority; cancel remains legal in that state).

## Tests

`ServerTaskStatusWriterTests` (token live / terminal-after-load yields /
churn-in-window retried / deleted row / custom guard / two racing writers →
exactly one wins / retry emits exactly one audit row). `AgentHubOwnershipTests`
gained the late-completion-vs-cancel race; `OfflineResultFailClosedTests`
gained the mid-ingest cancel (wrapper stream flips the row on first read —
deterministically inside the old race window).

Test traps: the fixture's default context does **not** register
`AuditLogInterceptor` — audit-count assertions need a bespoke context wired
like production; forcing a conflict deterministically = bump
`next_log_sequence` from a second connection inside the `apply` callback.

## Residuals / out of scope

- **Blue-green mixed-binary overlap**: a pre-B5 binary writing blindly during
  the upgrade window can still clobber the new binary's verdict (its UPDATE
  carries no token). Transitional by nature.
- `DeploymentTarget.Status` (online/offline marks) and adhoc-session statuses
  are separate tables with idempotent flips — not token-protected.
- No cooperative agent abort on cancel (B6): a cancelled wave still runs to
  completion on the agent; B5 only guarantees its late report cannot corrupt
  the verdict.

## References

- `docs/production-fix-prompts-2026-07-13.md` — B5 work package
- `docs/durable-dispatch.md` (B1), `docs/disconnect-reconciliation.md` (B3)
- Npgsql docs — [Concurrency tokens and xmin](https://www.npgsql.org/efcore/modeling/concurrency.html)
