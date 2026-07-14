# KrakenDeploy — Production-Readiness Audit

| | |
|---|---|
| **Version** | 1.0 |
| **Date** | 2026-07-13 |
| **Status** | Review |
| **Branch audited** | `fix/db-schema-hardening` @ `aefd694` (39 files uncommitted) |
| **Scope** | Full repo: 687 C# files / 18 projects / 32 docs / 1,157 tests |
| **Method** | Multi-agent parallel audit (architecture, security ×2, engine ×2, agent/transport, multi-account, UI/parity, ops, testing, docs) + owner spot-verification of the 4 highest-impact findings |
| **Verdict** | **Bettable foundation, not yet production-ready.** Concentrated, fixable gaps. The window to fix them cleanly is now. |

---

## 1. Executive verdict

The architecture is **sound and in several places genuinely ahead of Octopus Deploy.** This is not a toy: it builds clean, ships 1,157 mostly-integration tests (verified green), 122 Blazor pages, a full deploy pipeline, blue-green self-upgrade, envelope encryption, and an AI/MCP surface Octopus doesn't have. The parts that are *expensive to change later* — module boundaries, tenant isolation, the schema's invariants — are done well. The parts still outstanding are the parts still *cheap to change*. That is the right way round, and it is why betting on this is defensible.

**But it cannot go to production today.** Three classes of defect block it, all fixable before the "breaking changes allowed" window closes:

1. **Execution-engine resilience** — the core promise ("press deploy and walk away") is broken by non-durable dispatch, agents that never reconnect, deployments that strand in `Running` forever, and output variables that silently don't propagate between steps online. *Confirmed by three independent engine/transport passes.*
2. **A small set of high-severity security/correctness bugs** — an authenticated package-upload RCE, agents that self-assign privileged roles, sub-Space RBAC that isn't enforced at execution, and no secret-masking in logs.
3. **Broken on-prem packaging** — the recommended Docker deployment bricks encryption on first secret use, can't write its data volume, and ships an image with no `pg_dump`/`psql` so backup **and** restore fail.

None of these are architectural dead-ends. All are addressable. The honest framing: **you are ~6–10 focused engineering-weeks from a defensible on-prem v1**, most of it in the engine-resilience and packaging tiers, plus the decision to **defer multi-account SaaS** so the on-prem product ships clean.

---

## 2. Tier 0 — Production blockers (fix before ANY real deployment)

Every item here is a "silently wrong / permanently stuck / remotely exploitable" class defect on the primary path. Items marked **✓verified** were confirmed by direct code read.

### T0-1 — Non-durable dispatch queue, no crash recovery ✓verified-by-design
**Where:** `DeploymentService.cs:147`, `DeploymentWorker.cs:47`, `ScheduledDeploymentDispatchJob.cs:40-47`
Deployments are handed to the worker through an in-process `Channel<TenantWorkItem>`. The only re-scan job filters `ScheduledFor != null`. A process crash/restart between the `Queued` insert and channel consumption **strands the deployment in `Queued` forever**; a crash mid-run strands it in `Running`. No startup reconciler exists. Runbook runs share the flaw. *Confirmed by architecture + engine ×2 (3 agents).*
**Fix:** startup sweep (re-enqueue orphaned `Queued`, fail/interrupt orphaned `Running` with an audit row) **and** claim work via conditional `UPDATE … SET status='Running' WHERE status='Queued'`. Hangfire is already in-process and durable — route immediate dispatch through it, or add a `FOR UPDATE SKIP LOCKED` sweep.

### T0-2 — Agent abandons the tunnel after ~40s and never reconnects ✓verified
**Where:** `SignalRServerLink.cs:49` (bare `.WithAutomaticReconnect()` → `[0,2,10,30]s` then permanent close), `ServerLinkHostedService.cs:70` (`Task.Delay(Infinite)`), `Closed` handler only logs.
Any server restart, deploy, or network blip > ~40s takes the agent offline **until its process is manually restarted.** For a fleet, every server bounce silently disables every agent. **This interacts catastrophically with your blue-green self-upgrade: a zero-downtime *server* upgrade knocks the *entire agent fleet* permanently offline.** *Confirmed by engine + transport (2 agents) + owner read.*
**Fix:** custom `IRetryPolicy` with unbounded, jittered, capped backoff; supervise `Closed` in the hosted service and loop `StartAsync` until shutdown; re-send unreported completions on reconnect.

### T0-3 — Deployment strands in `Running` forever on agent drop (default config) — and wedges blue-green ✓verified-by-design
**Where:** `DeploymentWorker.cs:1667-1716` (wave await arms `CancelAfter` only when `TimeoutSeconds > 0`; default is **0 = unlimited**), `AgentHub.cs:101-131` (`OnDisconnectedAsync` never cancels pending sub-plan TCSs).
Agent dies mid-wave with default steps → the worker awaits the `TaskCompletionSource` with no deadline, forever. Second-order effect: the leaked dispatch keeps `InFlightWorkGauge > 0`, and `ReleaseDrainDecision.ShouldRetire` treats in-flight as "never retire" → **one dead agent blocks blue-green retirement indefinitely.** *Confirmed by engine ×2 + transport (3 agents).*
**Fix:** on `OnDisconnectedAsync`, `subPlans.Cancel(...)` every open slot for that target after the reconnect grace; enforce a server-side maximum wave/dispatch deadline independent of per-step config.

### T0-4 — Output variables silently don't propagate between steps on the online path
**Where:** `DeploymentWorker.cs:2088, 2438-2440`, `OutputVariableAccumulator.cs:44-47`
With the default `StartAfterPrevious` trigger, each step is its own wave; the target's variable bag is built **once** before any step runs and never re-merged with captured outputs. So `#{Octopus.Action[Step1].Output.Url}` in step 2 resolves to empty. Offline drops and runbooks work (whole plan dispatched at once), which pinpoints this as an online-split regression. **A switching Octopus user hits this on day one.** *Engine agent, high confidence.*
**Fix:** before each subsequent wave dispatch, merge `Octopus.Action[key].Output.*` into that wave's sub-plan `Variables`; or coalesce consecutive same-side steps into one sub-plan so the agent accumulator spans them.

### T0-5 — Authenticated arbitrary-file-write → RCE via package upload ✓verified
**Where:** `LocalPackageStore.cs:26` (`Path.Combine(dir, fileName)` + `FileMode.Create`, no sanitization), `PackageService.cs:25-33` (validates `packageId`/`version` but not `fileName`), endpoint `Program.cs:1156-1193`.
A non-admin user with `PackageEdit` uploads a file whose multipart filename is `..\..\..\inetpub\wwwroot\shell.aspx` (or an absolute path — `Path.Combine` lets a rooted second arg win). Writes a web shell / overwrites server binaries → code execution **as the service account, which holds the agent JWT signing key and can command every deploy target.** The artifact store already sanitizes; this is an inconsistency. *Confirmed by security ×2 (2 agents) + owner read.*
**Fix:** `fileName = Path.GetFileName(fileName)`, reject if it changed or is empty, mirror `LocalArtifactStore.SanitiseName`; defensively validate stored paths stay under `RootPath`.

### T0-6 — Plaintext secrets in task logs (no masking anywhere)
**Where:** `AgentHub.cs:225-265`, `TaskLogService`, `ScriptStepHandler.cs`, `ServerScriptStepRunner.cs`; root cause `DeploymentContracts.cs:9-23` (`Variables` is a flat dict with no sensitivity flag).
Sensitive variables are encrypted at rest but become **unmarked plaintext** in the plan, in every step process's env vars, in the server-side `%TEMP%` preamble, and in any script that echoes them — persisted verbatim in `task_step_logs`, shown on the log tab, downloadable. Octopus masks these. The team implicitly knows (the AI diagnosis path decrypts snapshot secrets specifically to redact them). **Most likely compliance blocker for RH state-sector / GDPR customers.** *Confirmed by engine + security (2 agents).*
**Fix:** add a sensitive-name set to `DeploymentPlan`; wrap the log callback in a redactor that replaces exact sensitive substrings with `***` before persistence, agent-side and server-side. Fold in output-variable sensitivity (see T1).

### T0-7 — On-prem backup AND restore are non-functional (`pg_dump`/`psql` absent from image) ✓verified
**Where:** `Dockerfile.server:38-44` installs only `curl`; `BackupCommands.cs:91`, `BackupEngine.cs:66-71` (nightly), `RestoreCommands.cs:175` all shell out to Postgres client tools that don't exist.
Following `deploy/onprem/README.md` → "pg_dump not found." The nightly schedule fails silently. **DR restore is impossible in the recommended deployment.** *Confirmed by ops agent + owner read.*
**Fix:** `apt-get install -y postgresql-client` (match Postgres major) in the runtime stage, or a backup sidecar; add CI that runs a real `backup`→`restore` round-trip in the on-prem image.

### T0-8 — On-prem `kraken-init` bricks encryption on first secret use
**Where:** `deploy/onprem/docker-compose.yml:36-56` (init sets neither `DOTNET_ENVIRONMENT` nor `Encryption__MasterKey`), `DatabaseCommands.cs:153-171` (defaults to Development → ephemeral-KEK branch → wraps DEK under a random key), `Program.cs:691` (prod boot never re-provisions).
Init wraps the DEK under an ephemeral KEK; the server boots in Production with the *real* KEK; first sensitive-variable access → GCM tag mismatch → `CryptographicException`. Recovery is non-obvious (`DELETE FROM data_encryption_keys` + re-setup). The web host does this correctly (fail-fast); only the CLI env-detection diverges. *Ops agent.*
**Fix:** add `Encryption__MasterKey: ${ENCRYPTION_KEY}` **and** `DOTNET_ENVIRONMENT: Production` to `kraken-init`.

### T0-9 — `Server:DataPath` unwritable + config-key split
**Where:** `docker-compose.yml:69,77-78` mounts `kraken-data:/data` (root-owned) but `Dockerfile.server` only chowns `/var/lib/krakendeploy`; six sites read bare `"DataPath"` (`Program.cs:280,1983`, `DeploymentWorker.cs:782`, `StepPackageService.cs:541`, `GrpcStepPackageDeliveryService.cs:84`, `OfflineDropBundleBuilder.cs:68`) vs `"Server:DataPath"` elsewhere.
The non-root process can't write the named volume; the DataProtection ring, step-packages, and offline bundles land in a *different, unwritable, unpersisted, un-backed-up* tree. The blue-green smoke compose already gets this right (`/var/lib/krakendeploy`). *Ops agent.*
**Fix:** standardize on `Server:DataPath` everywhere; mount at `/var/lib/krakendeploy`; set `DataProtection__KeyPath` to it.

---

## 3. Tier 1 — Correctness / security / safety (fix before you trust it or expose external users)

### Engine & concurrency
- **T1-1 Hub fallback overwrites `Cancelled`.** `AgentHub.cs:321-323` blindly writes `Succeeded`/`Failed` with no cancelled-guard (unlike the worker). A cancelled deployment silently flips back. *(engine ×2)*
- **T1-2 Double-dispatch — no atomic claim.** `CreateAsync` with a past `scheduledFor` both enqueues immediately AND persists `ScheduledFor`; the dispatch job re-enqueues during the worker's prep window → **plan executes twice.** `DeploymentService.cs:117,142-150`, `DeploymentWorker.cs:475-477`.
- **T1-3 Wave retry races the still-running attempt.** No agent abort; retry re-registers the TCS under the same key and re-sends the whole sub-plan → same wave runs twice on one machine, stale completion resolves the new attempt. `DeploymentWorker.cs:1667-1765`, `PendingSubPlanRegistry.cs:116-125`.
- **T1-4 Zero node-level concurrency control.** No task cap (Octopus defaults to 5), no per-target serialization; the agent runs unlimited concurrent plans. `LocalPackageCache` uses `FileMode.Create` with no temp+rename/checksum → truncated-zip extraction under concurrency. `DeploymentWorker.cs:67-73`, `LocalPackageCache.cs:43-61`.
- **T1-5 No optimistic concurrency token.** `ServerTask` has no `xmin`; `CancelAsync`, worker finalize, `FailAsync`, and the hub fallback all race last-writer-wins. An `xmin` token or terminal-state-guarded conditional UPDATE closes T1-1/T1-2/T1-5 in one move.
- **T1-6 Server-side steps never capture output variables.** `ServerScriptStepRunner` has no `OctopusMessageParser` interceptor; `Set-OctopusVariable` in a `RunOnServer` step captures nothing. Compounds T0-4.

### Security & authorization
- **T1-7 Agent self-assigns `Roles`.** ✓verified `AgentHub.cs:164-167`. A compromised `web` box registers with `Roles=["db","prod-secrets"]` → next deploy substitutes those scoped secrets into scripts sent to the attacker's box. Octopus assigns roles operator-side. **Fix: drop the assignment; report machine capabilities in a field that never feeds scoping.**
- **T1-8 Sub-Space RBAC not enforced at execution.** `RoleAssignmentScopeMatcher.cs:58-75` treats a null scope dim as an optimistic match; REST endpoints pass no scope; `DeploymentService.CreateAsync` has no evaluator. Only **1 of 123** `Guard.AllowAsync` sites passes a scope. A user scoped to "Test only" deploys to Prod via `POST /api/deployments`. *(security ×2)* **Fix: thread Project/Environment/Tenant scope into REST/CLI/MCP checks + re-check in mutating services; add a strict matcher mode where null ≠ auto-pass for writes.**
- **T1-9 MCP read tools have no RBAC.** Only `retry_deployment` calls `McpToolAuth`; `get_deployment_log`/`get_step_config`/etc. authenticate but don't authorize → any API key reads full logs (plaintext secrets per T0-6) and configs the REST equivalents gate. `DeploymentTools.cs`, `DeploymentLogResource.cs:32`.
- **T1-10 Manual-intervention gate silently auto-approves.** ✓ `BuiltInStepSchemas.cs:682-688`; `Permission.DeploymentApproveIntervention` exists with **zero `.razor` usages**. Operators believe they have an approval gate; the worker runs it unattended. *(UI + testing + engine)* **Fix: an "Awaiting approval" state + approve/reject panel gated by the permission; the worker must block on it.**
- **T1-11 Webhook SSRF via redirect + RFC1918.** `SsrfGuard` validates only the initial URL; `AllowAutoRedirect=true` follows a `302 → 169.254.169.254`; RFC1918 is allowed by design; non-2xx bodies are echoed into delivery history (readable SSRF). `WebhookTransport.cs:101-175`. Also un-guarded: catalog/OIDC/AI fetches. **For a segmented gov network, default-deny RFC1918 + no redirects + pin validated IP.**
- **T1-12 Agent JWT: 365-day HS256, no revocation, iss/aud validation off, cleartext-capable.** `AgentJwtService.cs:24`, `Program.cs:369-370`; `Http2UnencryptedSupport` set unconditionally; `agent.json` unprotected on Windows (chmod-600 only on Unix). **Fix: shorten lifetime + refresh, add a `TokenVersion` claim, flip iss/aud on, gate cleartext H2 to Dev, DPAPI-protect `agent.json`.** The "cert-based auth" in `docs/design-agent-enrollment-cert-auth.md` is design-only.
- **T1-13 No session revocation on offboard/password-reset.** No `SecurityStampValidator`, no revalidating auth-state provider, no user-disable flag, 7-day sliding cookie. `Program.cs:170-171,292-305`. High for a credentials product.
- **T1-14 DataProtection ring unencrypted at rest on Linux/HA** (`ProtectKeysWithDpapi` is Windows-only) → read the dir, forge auth cookies. **Use `ProtectKeysWithCertificate`/KMS for Linux/HA.**
- **T1-15 Offline results accepted unsigned when no per-target key is configured** (`OfflineResultService.cs:73-95`) — fail-open on an untrusted channel that drives DB writes. **Require a key for offline-drop targets.**

### Ops & platform
- **T1-16 Production drops all OpenTelemetry.** `Program.cs:544-569` gates console export on `IsDevelopment()`; no OTLP/Prometheus exporter for prod. Traces/metrics instrumented then discarded. **Add `AddOtlpExporter()` gated on `OTEL_EXPORTER_OTLP_ENDPOINT`.**
- **T1-17 Zero-downtime upgrade contradicts the migrations.** `self-upgrade-ha.md:88-95` mandates expand/contract; the migrations rename/drop/NOT-NULL. During a slot overlap or HA drain, a breaking migration faults the older release the instant it lands. **Adopt expand/contract for real, or drop the zero-downtime claim.**
- **T1-18 No DI validation fail-fast in prod** (`ValidateOnBuild`/`ValidateScopes` unset) — captive-dep/scope errors surface at first resolution. **Enable in all environments.**
- **T1-19 No `EnableRetryOnFailure`, no `MaxPoolSize`.** `ServiceCollectionExtensions.cs:98-112` — in-flight queries hard-fail on Postgres failover; a 2-node pool can exceed `max_connections=100`. **Add `NpgsqlRetryingExecutionStrategy` + a pool cap; enforce PgBouncer.**
- **T1-20 LAUS-specific: script encoding + shell defaults break on Croatian Windows.** `ScriptRunner.cs:92` writes `.ps1` UTF-8 **BOM-less** → Windows PowerShell 5.1 reads ANSI → **Croatian text corrupted**; the default step resolves to `pwsh`, which stock Windows Server lacks → default script step fails on a fresh target. **Write UTF-8-with-BOM; fall back to `powershell.exe` when `pwsh` is absent.**
- **T1-21 Agent self-upgrade: non-atomic swap, no rollback, no health-gate.** `AgentUpdateService.cs:267-274` (`Move` → `Copy` → `Exit(0)`; if `Copy` throws, the exe is gone). A bad build bricks the fleet. **Stage → verify (mandatory SHA) → atomic rename → keep `.old` → roll back on failed boot.**

---

## 4. Tier 2 — Now-or-never breaking changes (do while wire/schema contracts are still unfrozen)

These get 10× harder after v1 freezes the gRPC/SignalR/REST/EF surface.

1. **Finish the `server_tasks` engine merge.** The schema unified; the *engine* did not. `RunbookRunWorker.cs` is a 345-line degraded path (no waves, no server steps, no conditions/retries/timeouts, no cancel), while `RunbookRun.cs:13-16` and `ServerTask.cs:46-49` **document capabilities that don't run.** Route both kinds through one orchestrator; delete the second worker. Do it before real runbook history accumulates under the degraded path.
2. **Rename the "deployment" wire/enum surface to "task."** `DeploymentPlan`→`TaskPlan`, `DeploymentId`→`TaskId` (it literally carries a `RunbookRun.Id` today), collapse `DeploymentStatus` + `ServerTaskState` into one `TaskStatus`. These are frozen contracts at v1.
3. **Settle the process-snapshot location on `ServerTask`** (today: `Release.ProcessSnapshot` vs a nullable column on `server_tasks`) — inside the engine merge, not after.
4. **Promote control-flow flags out of the jsonb `Config` bag to typed columns** (`RunOnServer`, `MaxParallelism`, `ForEach.Collection/Parallel`) — a typo'd key silently changes control flow today. The M14 knobs were already promoted; finish the job.
5. **Extend the agent wire contract now:** a server→agent `CancelDeploymentAsync` (enables in-flight abort), an `AttemptId`/`DispatchId` on `DeploymentPlan` echoed in completions (dedupe re-dispatched waves, correlate completions to attempts), an integer `ContractVersion` in registration (detect version skew — `stepIndex` was added with no negotiation), and **reserve the enrollment/PoP surface** from the cert-auth design. Decide `resume_offset` (advertised in the proto, unimplemented on the client) — implement or remove before it's a permanent false promise.
6. **Split `Server.Data` → `Server.Data` (DbContext, configs, migrations, interceptors) + `Server.Application` (the 93 services, jobs, encryption).** Today anything touching persistence drags MailKit + the PowerShell SDK + the Anthropic client. Mechanical move now; a compatibility event later. Naturally fixes the `Mcp → Server.Transport` edge.
7. **Decouple ControlPlane/blue-green/HA from the `MultiAccount:Enabled` flag** and give the release registry a home in single-instance mode — so on-prem HA gets zero-downtime upgrades without standing up the SaaS catalog. The Router is already standalone; only the server wiring is entangled.
8. **Provision the per-account DEK schema now** (`data_encryption_keys.account_id` already exists) even if the feature ships later, so existing accounts don't need a rekey migration.
9. **Make the `DbContextFactory` shape mode-dependent** — pooled `Singleton` on-prem (currently forced `Scoped` unconditionally to support per-request account routing, so on-prem gets no context pooling).
10. **Add architecture-enforcement tests** (~50 lines, NetArchTest): Agent must not reference `Server.*`, Execution must reference nothing internal, Mcp must not reference Server.Transport, plus a Router↔ControlPlane SQL-schema contract test. Converts today's verified-true invariants into forever-true ones.

---

## 5. Strategic decisions

### 5.1 Multi-account SaaS — **defer and decouple for v1**
It **cannot boot** today (`Program.cs:263-270` correctly hard-throws because per-account DEK isn't implemented), its security-critical routing/provisioning boundary has **zero test coverage**, and it's **welded into the on-prem build** via a hard `Server → ControlPlane → Server.Data` reference — so your government on-prem product currently ships the entire SaaS attack surface (including a **plaintext tenant-connection-string secret store**, `FileSecretStore.cs`) for a feature that can't run. Quarantine it behind a compile-time boundary, harvest the excellent Router/blue-green for on-prem HA, and put v1 energy into the on-prem product you're actually replacing Octopus with. Revive SaaS as a deliberate v2 with the boundary tests as the gate condition.

### 5.2 Documentation — the README is actively harmful
`README.md:5` says *"early development — M1 walking skeleton in progress. Not yet usable in production"* while the product has blue-green, multi-account, envelope encryption, and a full deploy pipeline — it undersells by ~14 milestones **and** oversells two features that were never built (`tsvector`/`pg_trgm` search, the `Kraken` PowerShell helper module). `deploy/caddy/README.md` is **dangerous**: it tells operators the server auto-applies migrations on startup, but `Program.cs:653-657` only migrates under `IsDevelopment` → following that upgrade guide leaves a **production DB unmigrated.** Prereqs say .NET 9; the product is .NET 10. Missing entirely: an Octopus-migration/parity guide, agent production-install guide, `CONTRIBUTING.md`, `CHANGELOG.md`, `SECURITY.md`. Prune internal artifacts (`audit-2026-06-16.md`, `db-schema-fix-prompts-*.md`, stale `docs/erd/*.png`) before going public. **Docs readiness: 3/10** despite excellent internal design records.

---

## 6. What's genuinely better than Octopus (double down, market these)

1. **Schema-level tenant isolation** — global query filters **+ composite `(space_id, parent_id)` FKs + insert-stamping**. Octopus enforces Spaces in application code only; you enforce it in the database. Auditors love this.
2. **Router-based blue-green self-upgrade** — zero-downtime upgrade of the *deployment server itself* is Octopus's known weakness. The Router (557 LOC, standalone, advisory-lock-serialized transitions, degrade-stale cache) is a differentiator out of proportion to its size. *(Once T0-2/T0-3 stop it wedging.)*
3. **The CHECK-constrained `ServerTask` TPH spine** — Octopus's ServerTask is a loose document; yours is relationally enforced, so every future task kind (triggers, health checks, retention runs) is additive.
4. **OLAP pivot analytics + rearrangeable per-user dashboard** — Octopus Insights is fixed charts; yours is user-pivotable with saved views and drill-through.
5. **AI failure diagnosis + "suggest process"** — root-cause on failed deployments with highlighted log lines; process authoring assistance. Octopus has nothing equivalent.
6. **Outbound-only reverse-tunnel agents + Offline Drop with signed result bundle** — no inbound firewall holes on targets; a real air-gapped story. Directly relevant to LAUS-style segmented government networks.
7. **Step packages as a first-class, versioned, testable plugin SDK** — cleaner than Octopus's JSON-blob community templates.
8. **Octopus import on-ramp** (`ImportOctopusApiDialog`) — a direct migration path from the incumbent.

---

## 7. Test gaps (the safety net is good; these are the holes)

The suite is a real, integration-first safety net (~1,157 tests, 600+ against real Postgres via Testcontainers; every security bug in the history has a regression test; **the Docker/isolation tests DO run on the Linux CI leg** — only the Windows leg filters them). Confidence to make breaking changes is warranted. The holes:

- **No full transport round-trip test** (real `DeploymentPlan` over real SignalR → real agent → results back → finalize). Every leg is green in isolation, so a serialization/contract drift at the seam passes 100% of CI — exactly the risk when you make the T2-5 wire changes.
- **Destructive data-migrations have no data-correctness test** — on the very branch that ships `UnifyServerTasks` (`DROP` + `DELETE`) and the FK chain. Seed old-shape rows, migrate, assert survival.
- **Manual-intervention gate untested** (compounds T1-10).
- **Smoke tests only assert connectivity and run only on push-to-main, not PRs** — add a deploy-execution assertion and run on PRs.
- **DEK rotation has no "new encrypted store silently missed" guard** — add a reflection test asserting every `*Encrypted` column is in the rotation walk.

---

## 8. Suggested phased roadmap to on-prem v1

**Phase A — Stop-the-bleeding security (days).** T0-5 (package RCE, one line), T0-6 (log masking), T1-7 (agent role self-assign), T1-8 (sub-Space RBAC), T1-9 (MCP authz). Small, surgical, high-severity.

**Phase B — Engine resilience (the core bet, ~2–3 wks).** T0-1 (durable dispatch + startup reconciler), T0-2 (agent reconnect), T0-3 (disconnect reconciliation + wave deadline), T0-4 (online output vars), T1-1..T1-5 (cancel-guard, atomic claim, retry keying, concurrency cap, `xmin`). Add the transport round-trip test alongside.

**Phase C — Packaging & ops (~1 wk).** T0-7/T0-8/T0-9 (backup image, DEK init, DataPath), T1-16 (OTel export), T1-18/T1-19 (DI validation, Npgsql retry/pool), the migration data-correctness test.

**Phase D — Now-or-never breaking changes (~2 wks).** T2-1 (engine merge), T2-2 (task rename), T2-5 (agent wire contract), T2-6 (Server.Data split), T2-7 (decouple ControlPlane/HA), plus arch tests. Do these before contract freeze.

**Phase E — Parity & polish for the Octopus pitch.** Manual intervention (T1-10), live log streaming, the 7 stub tabs + orphaned StepPackages, deployment triggers, prompted variables, external feeds. Then rewrite the README and write the Octopus-migration guide.

**Phase F — Cut the v1 line.** Declare the schema baseline, switch to forward-only expand/contract migrations, freeze contracts, prove backup→restore E2E, flip agent JWT validation on. *This is where "breaking changes allowed" ends.*

---

## 9. Bottom line

The idea is good, the architecture is bettable, and the pieces that would be fatal to get wrong later (boundaries, isolation, schema invariants) are the pieces done best. What's outstanding is real but bounded and — critically — still cheap to change. Fix the engine-resilience tier, the handful of security bugs, and the packaging; defer multi-account; finish the engine merge and the wire-contract renames before you freeze. Do that, and "a better, self-hostable alternative to Octopus" is a claim the code can actually back.
