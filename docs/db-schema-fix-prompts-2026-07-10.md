# KrakenDeploy — DB Schema Hardening: 7 Opus Session Prompts

| | |
|---|---|
| **Status** | Review |
| **Version** | 1.2 |
| **Date** | 2026-07-10 |
| **Authors** | Domagoj Jugović, Claude (Fable 5 review session) |
| **Technologies** | .NET 10, EF Core 10, PostgreSQL, Blazor Server (Radzen) |
| **Projects** | KrakenDeploy.Server.Data, Server.Core, Server, Server.Transport, Cli, Mcp |
| **Source** | Consolidated 5-agent DB schema review, 2026-07-10 |

Seven sequential prompts for Opus 4.8 sessions. Each fix is one or more commits on a single
chain branch; each session starts where the previous one finished. Paste the **Common Context**
block plus the fix's prompt into a fresh session.

**Ordering (2026-07-10):** this chain runs to completion BEFORE finish-plan WP3 and all later
work packages — see the ordering update and merged execution order in
`docs/finish-plan-2026-07-05.md` §2 (v1.2). Reconciliation deltas applied there as per-WP
riders; deltas applied here: WP11 item 3 folded into fix 3, the log age-cap moved from fix 6 to
WP9, and fix 7's ERD regeneration removed (`docs/db-erd.md` was deleted as stale — do not
recreate it).

## Preconditions (manual, before Prompt 1)

1. Commit/merge `feat/deploy-release-ui` and `feat/dashboard-tile-layout` into `main` —
   **DONE 2026-07-10** (merge `4712364`; the dashboard branch was already in main's history;
   build 0/0 + full test suite verified green before the merge).
2. Create the chain branch — **DONE 2026-07-10**: `fix/db-schema-hardening` exists at `4712364`.
3. Dev DB content is disposable (destructive migrations are allowed throughout).
4. Docker Desktop must be started via Explorer, not plain `Start-Process`
   (`Start-Process explorer.exe -ArgumentList '"...Docker Desktop.exe"'`), or it dies mid-init.

---

## Common Context (paste at the top of every session)

You are working in `D:\_GITHUB\KrakenDeploy` (git repo — use `git`, never TFVC) on branch
`fix/db-schema-hardening`. KrakenDeploy is an Octopus Deploy clone: .NET 10 (SDK 10.0.300),
EF Core 10, PostgreSQL (snake_case), Blazor Server + Radzen, gRPC/SignalR agent transport,
Hangfire jobs, multi-account SaaS mode (DB-per-account; migrations are applied per tenant DB
by a fleet migrator — they must apply cleanly, but destructive changes and no data
preservation are explicitly allowed: this is pre-release).

Ground rules:
- Schema truth: `src/KrakenDeploy.Server.Data/Migrations/KrakenDbContextModelSnapshot.cs`.
  DbContext: `src/KrakenDeploy.Server.Data/KrakenDbContext.cs`. Entities:
  `src/KrakenDeploy.Server.Core/Domain/**`. Configurations:
  `src/KrakenDeploy.Server.Data/Configurations/**`.
- EF migrations: run `dotnet ef` with **startup project = KrakenDeploy.Server.Data**
  (design factory and EFCore.Design live there, not in Server) and pin `--framework net10.0`.
- Space isolation: `ISpaceScoped` + global query filter (`KrakenDbContext.ApplySpaceQueryFilters`)
  + `SpaceScopingInterceptor` stamping. `FindAsync` honors the filter. Never weaken this.
- Blazor: never `ConfigureAwait(false)` in component lifecycle; circuit-scoped caches must use
  `ConcurrentDictionary`; privileged circuit handlers re-check permission server-side via the
  `UiActionGuard` pattern (`Guard.AllowAsync(...)`), never trust UI-only gates.
- Code, comments, commit messages: English. No emoji.
- Definition of done for every session: solution builds with 0 warnings / 0 errors,
  full test suite green, docker smoke (`docker-compose.smoke.yml`) green when the fix touches
  the agent/worker path, and a final **adversarial self-review pass** over your own diff
  (hunt for cross-space leaks, missed call sites, migration ordering bugs) with findings fixed.
- Commit granularity: small, reviewable commits with imperative messages; do not squash.
- Do NOT touch `subscription_poller_state` audit churn — a separate session owns that fix.

---

## Prompt 1 — Audit log Space filter (security) — DONE 2026-07-10 (b4424f7…a1266de)

**Problem.** `audit_entries` carries a nullable `space_id` but is not `ISpaceScoped` and nothing
filters it. Two leak paths let a Space-A operator with `EventView` read Space B's audit rows,
including full `BeforeJson`/`AfterJson` entity snapshots:
- `src/KrakenDeploy.Server/Components/Pages/Audit.razor` (~line 253): `BuildQuery()` is
  `Db.AuditEntries.IgnoreQueryFilters()` plus date/text filters only. The `IgnoreQueryFilters()`
  is a no-op that reads as if scoping existed.
- `/api/audit/export.csv|.json` in `Program.cs` (~lines 1544–1566): `RequirePermission(EventView)`
  is evaluated against the caller's *current* Space, then `Services/AuditExportService.cs`
  streams every row. Its `Filter` record has no Space dimension.

**Decisions (final, do not relitigate):**
- Visibility = **active Space only**. Rows with `space_id IS NULL` (system events) are visible
  only to `AdministerSystem` holders (give sysadmins an "include system events" toggle on the page).
- `AuditExportService.Filter` gains a **required** `SpaceIds` member; every caller must pass
  Space ids validated against `PermissionEvaluator.GetAccessibleSpaceIdsAsync` — make it
  impossible to construct the filter without a Space decision.
- Create one query choke point (extend `AuditExportService` or add an `AuditQueryService`) used
  by both the page and the export endpoints: `WHERE space_id = @active OR (space_id IS NULL AND @isSysAdmin)`.

**Also ride along:** add index `(subject_type, subject_id, occurred_utc)` on `audit_entries`
("history of this object" queries currently seq-scan the largest table).

**Tasks.** Implement the choke point; rewrite the page query and both export endpoints; grep for
every other `AuditEntries` read in `src/` and route it through the choke point or justify it in
a code comment; migration for the index; tests: Space-A user sees zero Space-B rows via page
query and via both exports, non-sysadmin never sees NULL-space rows, sysadmin toggle works.
No docker smoke needed (no agent path). Adversarial review focus: any remaining unfiltered
audit read, and the export endpoints' parameter binding.

---

## Prompt 2 — Retire `deployments.target_id`, unify target delete policy — DONE 2026-07-10 (560f0d2…9451cb6)

**Problem.** `deployments.target_id` is a documented transitional column duplicating
`deployment_target_assignments` (every deployment row has assignment rows — historical backfill
done). Two authorities for "which targets does this deployment hit", with inconsistent delete
behavior: `deployments.target_id` SET NULL, `deployment_target_assignments.target_id` RESTRICT,
`deployment_step_outcomes.target_id` bare column (no FK, dangles).

**Decisions (final):**
- Drop `deployments.target_id`, its navigation, and the `(release_id, environment_id, target_id)`
  index (replace with `(release_id, environment_id)` if query analysis warrants).
- Delete policy: **RESTRICT everywhere execution history references a target** — keep
  assignments RESTRICT, make `runbook_runs.target_id` RESTRICT (currently SET NULL), and give
  `deployment_step_outcomes.target_id` a real FK RESTRICT. A target with history becomes
  undeletable; the soft-disable escape hatch (archived flag) lands in fix 4 — out of scope here.
- Fix the known CLI bug while in there: `--target` missing maps null→`Guid.Empty` instead of
  erroring (deploy verb in `src/KrakenDeploy.Cli`).

**Known readers to migrate to the assignments join** (grep to confirm the list is current):
`DeploymentWorker.cs:212` (dispatch fallback), `AgentDeploymentOwnership.cs:28-36`,
`AgentPackageEntitlement.cs:106`, `DashboardService.cs:129`, `TargetHealthBuilder.cs:53`,
`DeploymentDetail.razor:679`, `DeployReleaseStepRunner.cs:149`, `Mcp/DeploymentTools.cs:162`,
`OfflineResultService.cs:188`; writer: `DeploymentService.cs:115,125-127`.

**Tasks.** Migrate every reader/writer, drop the column via migration, apply the FK/delete-policy
changes, update tests. **Docker smoke required** (agent ownership/entitlement path changes).
Adversarial review focus: single-target UX paths (CLI, MCP, offline drop) that silently assumed
`TargetId != null`, and the worker's dispatch fallback semantics.

---

## Prompt 3 — Unify processes and executions into `server_tasks` (the big one)

**Problem.** Deployment and runbook worlds are forked at the DB: `deployment_steps` vs
`runbook_steps` drifted (runbook steps lack `condition`, `condition_variable_expression`,
`required`, `max_retries`, `retry_delay_seconds`, `timeout_seconds`, `start_trigger`;
`target_roles` is `text[]` vs `jsonb`; lengths differ), `deployment_processes`/`runbook_processes`
are payload-free identity tables, and `runbook_runs` lack failure mode, scheduling, multi-target
assignments, artifacts, output variables and step outcomes. Runtime hacks paper over it:
`AgentHub.cs:232` probes "Deployment first, then RunbookRun (same ID space)";
`AgentHub.cs:492-496` **silently drops** runbook output variables. Octopus has one process shape
and one ServerTask spine — that is the target.

**Decisions (final):**
1. **One `server_tasks` table** replacing `deployments` + `runbook_runs`: `kind` discriminator
   (Deployment | RunbookRun), nullable `release_id` / `runbook_id` with a
   `CHECK` (exactly one non-null matching `kind`). Choose the EF mapping (TPH with a shared base
   class, or single entity with Kind) — but one table, and keep a typed API surface for services.
2. **One `processes` table** (`owner_kind`, `owner_id`, unique per owner) + **one `process_steps`**
   table with the FULL column set (all execution knobs, `text[]` target_roles, unified lengths:
   name 256 / step_type 128 / package_id 256). Drop the four old process/step tables.
3. **Unify children** on `task_id`: `task_step_outcomes`, `task_output_variables`,
   `task_artifacts`, `task_target_assignments`. All FK CASCADE from `server_tasks`.
   **Logs — REVISED DECISION 2026-07-10 (v1.2, supersedes "unified line rows"): hybrid
   staging → blob-per-step.** Rationale: Postgres cannot append to a TOASTed value (every
   append rewrites the whole blob), so a pure blob model breaks live streaming; pure line rows
   make logs the biggest table + index + WAL + backup burden in the system. Shape:
   - `task_log_live` (staging): line rows — task_id, step_index, target_id (nullable),
     sequence, level varchar(16), timestamp, message; unique (task_id, sequence); FK CASCADE
     from server_tasks. The existing streaming write paths and the live-tail UI read/write
     ONLY this table while a task runs. Logged table (not UNLOGGED) — crash must not lose logs.
   - `task_step_logs` (final): ONE row per completed step(×target): task_id, step_index,
     target_id (nullable), content text — lines serialized as `seq|iso8601-ts|level|message`
     per line, TOAST/lz4 compresses transparently and ILIKE keeps working — plus summary
     columns: line_count, error_count, warn_count, first_error_line, byte_size, completed_utc.
     FK CASCADE from server_tasks.
   - Compactor: on step completion move that step's staging lines into one blob row and delete
     them; at terminal task status, sweep-compact any staging remainder. Offline result import
     and runbook-kind tasks write through the same compactor.
   - Read path: task detail stitches completed blobs + remaining staging; per-task level/text
     filtering happens in memory (per-step blobs are small). NO global cross-task log search
     in-app: queryable facts (package pins, initiator, error counts) live in structured
     columns/snapshots, and global text search is the WP10 Seq pipeline. Do NOT add trgm/GIN
     indexes over log content.
4. **Full parity**: runbook runs gain failure mode, scheduling, multi-target assignments,
   artifacts, output variables (fix the AgentHub drop), step outcomes — schema + worker/AgentHub
   wiring + UI (runbook step editors gain condition/retry/timeout/start-trigger fields mirroring
   the deployment step editor; runbook run detail shows artifacts/output vars/outcomes).
5. **Denormalize** `project_id` (NOT NULL) and `channel_id` (nullable) onto `server_tasks`,
   stamped at creation — dashboards/pivot/matrix drop the task→release→project join.
6. Add inert `form_values jsonb NULL` (future prompted variables; written as NULL for now).
7. Delete policy: `server_tasks → releases` RESTRICT and `server_tasks → runbooks` RESTRICT
   (execution history is delete-proof on both sides — this fixes the current asymmetry where
   runbook history mass-cascades).
8. `StepSnapshot`/`DeploymentPlanFlattener`/`StepConditionEvaluator` in KrakenDeploy.Execution
   stay shared and Octostache-only; the agent must never gain a Server.* reference.
9. While unifying log allocation, route `ServerScriptStepRunner`'s unguarded
   `deployment.NextLogSequence++` (~line 246) through the same sequencer the agent path uses —
   parallel server-side steps can currently take duplicate sequence numbers. This closes
   finish-plan WP11 item 3; leave a regression test.

**Sequencing inside the session (separate commits):** (a) schema + entities + migration,
(b) engine — DeploymentWorker/RunbookRunWorker convergence, AgentHub single lookup, terminal
status resolver, cancel semantics preserved, (c) services + UI (including analytics/pivot SQL
and the Projects matrix now reading `kind = Deployment`), (d) tests + smoke.

**Verification:** build 0/0, full tests, **docker smoke required** (`docker-compose.smoke.yml`;
run `docker-compose.smoke-bluegreen.yml` too if time allows — the drain watcher reads task
state). Adversarial review focus: every `Deployments`/`RunbookRuns` DbSet consumer, Space
stamping on the new children, offline drop bundle and MCP tools paths, and the wave/cancel
state machine surviving the rename.

---

## Prompt 4 — FK hardening wave

**Problem.** A layer of integrity the iterative design skipped: bare Guid columns without FKs,
`ISpaceScoped` entities missing their Space FK, nullable-key unique indexes that admit
duplicates, and the security-authority table storing scope as FK-less jsonb arrays.

**Decisions (final) — implement all:**
1. **User rows cascade.** Real FKs `ON DELETE CASCADE` to `users` from: `api_keys.user_id`,
   `team_members.user_id`, `pivot_views.user_id`, `project_dashboard_views.user_id`,
   `dashboard_layouts.user_id`. `audit_entries.user_id` deliberately stays FK-less (forensics
   outlive users) — leave it. Keep the service-layer cleanup in `UserService.DeleteAsync` as
   belt-and-braces.
2. `tenants.variable_set_id`: FK `ON DELETE SET NULL` + index (do NOT invert ownership).
3. `deployment_diagnoses` (now keyed to `server_tasks` after fix 3): FK CASCADE on the task id.
4. **ConfigureSpaceScope stragglers** — apply the standard Space FK + index to: `adhoc_sessions`,
   `ai_call_logs`, `deployment_diagnoses`, `deployment_freezes`, `pivot_views`,
   `project_dashboard_views`, `dashboard_layouts`. Skip `space_ai_settings` (fix 7 drops it).
   Drop standalone `(space_id)` indexes where a `space_id`-leading composite already exists.
   Add a model-validation test: every `ISpaceScoped` entity type must have an FK to `spaces`.
5. `email_digest_outbox.subscription_id` and `subscription_deliveries.subscription_id`:
   FK CASCADE to `event_subscriptions`. Also drop `subscription_deliveries.attempt_number`
   (dead — the unique `(subscription_id, event_id)` idempotency key forbids retry rows) and fix
   the contradicting XML doc on the entity.
6. `users.last_oidc_provider_id`: FK SET NULL + index.
7. **NULLS NOT DISTINCT** (precedent: `DataEncryptionKeyConfiguration`) on:
   `teams (space_id, name)`, `team_external_groups (team_id, identity_provider_id, group_claim)`,
   and the step-outcome upsert key `(task_id, step_index, target_id)`.
8. Filtered unique: one default channel per project (`UNIQUE (project_id) WHERE is_default`).
9. **Lifecycle delete = RESTRICT** while referenced by `projects.lifecycle_id` or
   `channels.lifecycle_id` (currently SET NULL, which silently un-gates deploys).
10. `projects.project_group_id`: backfill to the Default Project Group and make NOT NULL
    (the M10 transition never finished).
11. **`role_assignment_scopes` child table** replacing the jsonb Guid arrays on
    `role_assignments` (`project_group_ids`, `project_ids`, `environment_ids`, `tenant_ids`) —
    shape: `(role_assignment_id FK CASCADE, dimension, ref_id)` with per-dimension FKs CASCADE to
    the referenced tables, so deleted projects/environments vanish from grants instead of
    lingering. Preserve the matcher semantics exactly: *no rows for a dimension = matches all*.
    Do NOT carry a tag dimension (fix 7 drops `tag_ids` as dormant). Rewrite
    `RoleAssignmentScopeMatcher` and the team detail UI accordingly.
12. `environments` gains an `archived` flag (RESTRICT + no soft-delete currently makes any
    environment with history permanently undeletable); hide archived environments from pickers.
    Add a cleanup interceptor (pattern: `TagApplicationCleanupInterceptor`) sweeping deleted
    environment ids out of `lifecycles.phases`, `deployment_freezes.environment_ids`, and
    `event_subscriptions.environment_ids`.

**Verification:** build 0/0, full tests (add constraint-violation tests for the new FKs and
NULLS NOT DISTINCT keys; Docker-gated integration tests are acceptable — they are skipped on
Windows CI by convention). No smoke needed unless the step-outcome key change touches the agent
write path — check, and run smoke if it does. Adversarial review focus: RBAC matcher semantics
(empty-dimension = all) and cascade blast radius on user delete.

---

## Prompt 5 — Composite Space FKs (cross-Space integrity in the DB)

**Problem.** Space integrity below the query filter is convention-only: zero check constraints,
zero composite FKs. Nothing in the DB prevents a child row in Space A referencing a parent in
Space B; one write path already misses the app-layer convention
(`VariableService.IncludeSetAsync`, `VariableService.cs:385-409`, inserts a project↔variable-set
link without validating either id against the filtered sets).

**Decisions (final):**
1. Add `UNIQUE (space_id, id)` on every space-scoped parent table.
2. Convert child FKs to composite: `FOREIGN KEY (space_id, parent_id) REFERENCES parent
   (space_id, id)`, keeping each FK's existing delete behavior. Children **replace** their direct
   FK to `spaces` with the composite parent FK (transitively guaranteed); aggregate roots keep
   their direct `spaces` FK. Every space-scoped child already carries `space_id` — no data motion.
3. The join tables (`target_environments`, `target_tenants`, `project_tenants`,
   `project_variable_set_links`, `task_target_assignments`) gain a stamped `space_id` + composite
   FKs to **both** sides. Convert the implicit EF many-to-many joins to explicit join entities
   while you're there (also fixes the auto-generated `projects_id`-style column names — name the
   columns properly).
4. Fix `IncludeSetAsync`: validate both ids against the filtered `Projects`/`VariableSets` sets
   before insert (belt — the composite FK is the braces).
5. Priority order inside the migration if you need to split: `releases→projects`,
   `server_tasks→*`, `variables→variable_sets`, `tags`/`tag_applications`→`tag_sets`, then the rest.

**EF note:** composite FK principals need either `HasAlternateKey(space_id, id)` or the unique
index + `HasPrincipalKey`. Prefer `HasPrincipalKey` over alternate keys to avoid EF treating the
pair as an identity everywhere.

**Verification:** build 0/0, full tests; add integration tests proving a cross-space insert now
fails with an FK violation (Docker-gated is fine). Migration must apply cleanly on an existing
dev DB (fleet migrator). No smoke required. Adversarial review focus: any service that
constructs entities with `SpaceId` unset expecting the interceptor to stamp it AFTER relationship
fixup — the composite FK makes ordering visible.

---

## Prompt 6 — Execution provenance + log retention

**Problem.** `server_tasks` has no initiator/cause columns — once triggers and real manual
interventions land, history cannot be backfilled. Log rows (one per line, in Postgres) currently
outlive any retention policy except whole-execution pruning.

**Decisions (final):**
1. `server_tasks` gains: `created_by_user_id` (FK to `users`, SET NULL — provenance must survive
   user deletion), `created_by_display` varchar (denormalized name, survives everything), and
   `cause` int enum: `Manual`, `Api`, `Cli`, `Mcp`, `Scheduled`, `ParentStep` (deploy-release
   step child), `OfflineImport`, with room reserved for `Trigger`. Optional `cause_detail`
   varchar(256) for e.g. the parent task id or API key name.
2. Stamp at **every** creation site: `DeploymentService`, runbook run creation, CLI verbs, MCP
   tools, `ScheduledDeploymentDispatchJob`, `DeployReleaseStepRunner` (child deployments),
   offline result import. Enforce with a service-layer guard (creation API requires a cause).
3. UI: show "initiated by / cause" on task detail pages and the deployments dashboard column.
4. **Log retention (rows stay in Postgres — decided):** verify pruned executions delete their
   children via the fix-3 CASCADE FKs and add a regression test (including logs of pruned
   runbook-kind tasks). The age-based cap for logs of *retained* executions is deliberately NOT
   in this fix — it moved to finish-plan WP9 (retention expansion) so `RetentionService` is
   extended exactly once. Do not add retention knobs here.

**Verification:** build 0/0, full tests, **docker smoke required** (creation-site changes touch
the worker/agent path via scheduled dispatch and parent-step deploys). Adversarial review focus:
a creation site you missed (grep for `new ServerTask`/`Add(` on the task DbSet), and retention
deleting logs of still-running executions.

---

## Prompt 7 — Unified settings table, dead-column drop, channel rule enforcement

**Part A — `settings` table.** Fold six tables into one:

```sql
CREATE TABLE settings (
    id            uuid PRIMARY KEY,
    scope_type    smallint NOT NULL,          -- 0=System, 1=Space (2=User reserved)
    scope_id      uuid NULL,                  -- NULL for System
    key           varchar(128) NOT NULL,      -- 'smtp','backup','maintenance','performance','features','ai'
    payload       jsonb NOT NULL,
    created_utc   timestamptz NOT NULL,
    modified_utc  timestamptz NULL
);
CREATE UNIQUE INDEX ux_settings_scope_key
    ON settings (scope_type, scope_id, key) NULLS NOT DISTINCT;   -- precedent: data_encryption_keys
```

Decisions (final):
- Fold in: `smtp_settings`, `backup_settings`, `maintenance_settings`, `performance_settings`,
  `feature_flags` (ONE System-scope document holding `Dictionary<string,bool>` of overrides;
  delete-on-default becomes remove-entry), `space_ai_settings` (Space scope). Data-motion
  `INSERT INTO settings SELECT ...` migrations, then drop all six tables.
- Payloads are **typed POCOs in Server.Core** implementing `ISettingsDocument` (static `Key`,
  static `Scope`), serialized with `JsonStringEnumConverter`. Secrets stay ciphertext strings in
  members named `*Encrypted`, encrypted by the calling service exactly as today. This naming is
  load-bearing: extend `DekRotationWalk` with a generic settings step (load rows, deserialize by
  registered type, re-encrypt `*Encrypted` members, reserialize) and extend
  `DekRotationCompletenessTests` to reflect over `ISettingsDocument` implementations — without
  this, a DEK rotation silently bricks SMTP/AI secrets.
- Single accessor `SettingsService` (`GetAsync<T>(scopeId?)` returning `new T()` when the row is
  missing — property initializers are the backfill; `SaveAsync<T>` upserting by scope+key with
  cache invalidation). `ConcurrentDictionary` TTL cache. `UseXminAsConcurrencyToken()` on the
  entity. Stamp audit `SubjectName = key` so audit entries stay distinguishable.
- Space caging: the table is NOT `ISpaceScoped` (nullable scope) — scoping lives only in
  `SettingsService`. Add an architecture test asserting no reference to the settings DbSet
  outside `SettingsService`.
- Existing domain services (`SmtpSettingsService` with its probe/null-keeps-password semantics,
  `SpaceAiSettingsService` with audited reveal, `FeatureFlagService`, etc.) keep their public
  surfaces and permission checks; only their persistence swaps. The MCP gate switches from its
  `Select(s => s.McpEnabled)` projection to the cached document.
- UI: build the unified settings admin page (System-scope documents on one Configuration page
  with per-section save; Space AI settings stay on their Space page).

**Part B — dead-column drop migration (all verified dead, zero behavioral impact):**
`deployment_diagnoses.{model_used, prompt_tokens, completion_tokens}`;
`adhoc_iterations.{llm_model, llm_prompt_tokens, llm_completion_tokens}` (fix
`AdhocSessionPersistenceTests` which is the only writer); `identity_providers.{icon_url,
sort_order}` (login-page ordering falls back to name — it already effectively does);
`role_assignments.tag_ids` and `deployment_freezes.tag_ids` (dormant end-to-end; fix 4's scope
table deliberately has no tag dimension). Also: remove `SpaceStatus.Suspended` (never assigned;
`Spaces.razor` renders a badge for a state that cannot exist) and filter the identity-provider
type dropdown to implemented types (selecting Ldap/ADFS today produces a broken OIDC registration).

**Part C — enforce channel version rules.** `channels.version_range`/`version_tag` are stored
and displayed but never applied. Implement Octopus-semantics validation at release creation in
`ReleaseService` (and CLI/MCP create paths): package versions must satisfy the SemVer range and
the pre-release tag regex of the release's channel; clear validation errors in the UI dialog.

(There is no Part D — `docs/db-erd.md` was deleted as stale on 2026-07-10; do not recreate it.)

**Verification:** build 0/0, full tests (DEK rotation completeness + settings round-trip +
channel rule tests), no smoke needed unless the MCP gate change touches the agent path (it does
not — verify). Adversarial review focus: DEK rotation coverage of every `*Encrypted` member,
maintenance-mode read path performance (it runs per-request), and feature-flag concurrency on
the single document row.

---

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-07-10 | Initial: 7 prompts from the consolidated 5-agent schema review |
| 1.1 | 2026-07-10 | Preconditions done (merge `4712364`, chain branch created); reconciled with finish-plan v1.2: WP11 item 3 folded into fix 3 (decision 9), log age-cap moved fix 6 → WP9, ERD regeneration removed (db-erd.md deleted); chain ordered before WP3 |
| 1.2 | 2026-07-10 | Fixes 1+2 DONE on the chain; log storage decision REVISED (grill session): fix 3 builds hybrid staging→blob (`task_log_live` + `task_step_logs`, text blob with `seq\|ts\|level\|` prefix, compactor at step/terminal, per-task search only — global search = WP10 Seq) instead of unified line rows |
