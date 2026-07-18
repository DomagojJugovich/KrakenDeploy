# KrakenDeploy — Production-Fix Work Packages & Opus Prompts

| | |
|---|---|
| **Version** | 1.2 |
| **Date** | 2026-07-13 (Status column + progress tracking added 2026-07-16) |
| **Status** | Archived |
| **Source** | [production-readiness-audit-2026-07-13.md](production-readiness-audit-2026-07-13.md) (finding IDs T0-*/T1-*/T2-* below refer to it) |
| **Assumes merged first** | `finish-plan-2026-07-05.md` WP1–WP15 **and** the `fix/db-schema-hardening` chain. Line numbers in the audit will have shifted — **re-locate every anchor by symbol name, not line.** |

> **ARCHIVED 2026-07-18** — superseded by [master-plan-2026-07-18.md](master-plan-2026-07-18.md). Prompts for completed WPs remain here for reference; do not execute open-WP prompts from this file — they contain verified-stale claims.

---

## 0. How to use this

Same protocol as `finish-plan-2026-07-05.md §4`: each work package is a self-contained prompt for a fresh Claude Code session on **Opus 4.8**. Paste, in order:

1. The **Common preamble** from `finish-plan-2026-07-05.md §4` (unchanged — house rules 1–11 still apply).
2. The **Audit addendum** (§2 below).
3. The **WP prompt**.

One WP per session/branch. Branch names suggested per WP. Build + affected tests + `dotnet run` boot before finishing (preamble rule 9). Report honestly what was verified vs not (rule 10).

**Sequencing:** Phase A (security fast-fixes) and Phase B (engine resilience) are the two that block production — do them first, in parallel across sessions where the dependency table allows. Phase C (packaging) can run in parallel with A/B. Phase D (now-or-never breaking changes) should land **before the v1 contract freeze** but after B (it builds on the engine work). Within a phase, respect the `depends-on` column.

---

## 1. Dependency & sequence table

| WP | Title | Status | Tier | Findings | Size | Depends-on |
|---|---|---|---|---|---|---|
| **A1** | Package-upload path sanitization | ✅ Done | T0 | T0-5 | XS | — |
| **A2** | Secret masking in logs + sensitivity plumbing | ✅ Done | T0 | T0-6, T1-6(out-vars) | M | — |
| **A3** | Remove agent role self-assignment | ✅ Done | T1 | T1-7 | S | — |
| **A4** | Enforce sub-Space RBAC on the execution surface | ✅ Done | T1 | T1-8 | L | — |
| **A5** | MCP read-tool authorization | ✅ Done | T1 | T1-9 | S | — |
| **A6** | SSRF hardening (redirects, RFC1918, catalog/OIDC/AI) | ✅ Done | T1 | T1-11 | M | — |
| **A7** | Auth-session hardening (revocation, DP-ring, cookie) | ✅ Done | T1 | T1-13, T1-14, M2 | M | — |
| **A8** | Agent transport auth hardening | ✅ Done | T1 | T1-12, T1-15 | M | B6 (wire) helps but not required |
| **B1** | Durable dispatch + startup reconciler + atomic claim | ✅ Done | T0 | T0-1, T1-2 | L | — |
| **B2** | Agent reconnect: unbounded + supervised | ✅ Done | T0 | T0-2 | S | — |
| **B3** | Disconnect reconciliation + server-side wave deadline | ✅ Done | T0 | T0-3 | M | B2 |
| **B4** | Online cross-step output variables + server capture | ✅ Done | T0 | T0-4, T1-6 | M | — |
| **B5** | Optimistic concurrency + cancel-guard all writers | ✅ Done | T1 | T1-1, T1-5 | M | B1 |
| **B6** | Agent abort + attempt-idempotency wire contract | ✅ Done | T2 | T2-5, T1-3 | L | — |
| **B7** | Node concurrency cap + safe cache + retry re-resolve | ✅ Done | T1 | T1-3, T1-4 | M | B6 |
| **B8** | Server↔agent transport round-trip test | ✅ Done | test | §7 gap | M | B1–B7 land first |
| **C1** | Backup/restore image + round-trip CI | ⬜ Open | T0 | T0-7 | S | — |
| **C2** | On-prem compose: DEK init + DataPath unify | ✅ Done | T0 | T0-8, T0-9 | M | — |
| **C3** | Production hardening (DI validate, Npgsql, healthz) | ✅ Done | T1 | T1-18, T1-19, P1 | S | — |
| **C4** | Migration data-correctness tests + expand/contract | ⏸ Deferred | T1 | T1-17, §7 gap | M | migration consolidation (see §C4) |
| **C5** | Windows/Croatian script correctness (BOM + pwsh) | ✅ Done | T1 | T1-20 | S | — |
| **C6** | Agent self-upgrade atomicity + rollback | ⬜ Open | T1 | T1-21 | M | — |
| **D1** | Finish server_tasks ENGINE merge | ⬜ Open | T2 | T2-1, T2-3 | XL | B1–B5 |
| **D2** | Rename Deployment→Task wire/enum surface | ⬜ Open | T2 | T2-2 | L | D1 |
| **D3** | Promote control-flow config keys to columns | ⬜ Open | T2 | T2-4 | M | — |
| **D4** | Split Server.Data → Data + Application | ⬜ Open | T2 | T2-6 | L | — |
| **D5** | Decouple ControlPlane/HA from MultiAccount flag | ⏸ Deferred | T2 | T2-7, strategic | L | — |
| **D6** | DbContext factory mode-dependent pooling | ⬜ Open | T2 | T2-9 | S | D5 |
| **D7** | Architecture-enforcement tests | ⬜ Open | T2 | T2-10 | S | D4, D5 |

Sizes: XS ≈ <½ day, S ≈ ½–1 day, M ≈ 1–3 days, L ≈ 3–6 days, XL ≈ 1–2 wks.

**Status legend / progress** (as of 2026-07-16 — reflects memory records + code evidence; all Done WPs are on `main` **local, not pushed**):

- ✅ **Done (19):** A1–A8, B1–B8, C2, C3, C5. *(Plus the on-prem DataProtection-cert enablement, which is not a lettered WP.)*
- ⬜ **Open (8):** C1, C6, D1, D2, D3, D4, D6, D7.
- ⏸ **Deferred (2):** C4 (blocked on migration consolidation — see §C4), D5 (strategic multi-account defer per the audit).

> Provenance note: A2/A3 completion is **inferred from code** (`KrakenDeploy.Contracts/Logging/SecretRedactor` + `RequestLogRedaction` wired into the deployment log/output path; `AgentHub` no longer assigns `Roles`), not from a dedicated WP record — confirm coverage if in doubt. A4 was verified in code 2026-07-16 (originally found Partial) and **COMPLETED 2026-07-16** (see the §A4 status block): the strict matcher + `EnsureScopedAsync` service authority now cover the full mutating/execute surface — deployment create/cancel, process/variable edits, release create + update-variables, runbook create/rename/delete + step edits + run + run-cancel, drop-bundle regenerate, and tenant ops — with `SubSpaceRbacExecuteTests` + `RoleAssignmentScopeMatcherTests`, and the REST endpoints map `AuthorizationException` → 403.

---

## 1a. Deduplication against finish-plan & db-schema-hardening (READ THIS)

These audit findings are **already scoped elsewhere — do NOT create prompts for them here**, but verify the planned WP actually closes them:

- **Manual intervention auto-approves (T1-10)** → finish-plan **WP3**. Ensure WP3's worker actually *blocks* on approval and wires `Permission.DeploymentApproveIntervention` (today it has zero `.razor` usages).
- **Production drops OpenTelemetry (T1-16)** → finish-plan **WP10**. C3 below assumes WP10 landed; C3 only adds the DI/Npgsql/healthz pieces.
- **Triggers / prompted vars / retention / stub tabs / StepPackages nav** → finish-plan **WP4, WP6, WP7, WP8, WP9**. Not repeated here.
- **`server_tasks` SCHEMA unification + FK hardening** → db-schema-hardening chain. **D1 is the ENGINE merge that follows it** — a separate, still-open piece (the schema unified; `RunbookRunWorker` is still a degraded path).

Two planned items need **reconsideration in light of this audit** — flag to the owner, don't blindly execute:

- **finish-plan WP12 "Per-account DEK: unblock multi-account boot"** — the audit recommends **deferring multi-account for v1** (§5.1 of the audit). If you accept that, **do NOT unblock the boot for v1.** Keep the `Program.cs` fail-fast. WP12's effort moves into **D5** (decouple/quarantine). If you reject the defer, WP12 stands but must also close A8's agent-JWT and the `FileSecretStore` plaintext-secret issue before any real tenant.
- **finish-plan WP14 "Documentation reconciliation"** — expand its scope to also fix the two audit doc-defects: (a) **`deploy/caddy/README.md` is dangerous** — it claims the server auto-applies migrations on startup, but `Program.cs` only migrates under `IsDevelopment`, so its upgrade steps leave a prod DB unmigrated; (b) the **README status line** ("M1 walking skeleton, not usable in production") and the **.NET 9 → .NET 10** prereq drift across README/on-prem-guide/deploy READMEs; (c) add an **"Coming from Octopus" parity/migration guide**, `CHANGELOG.md`, `SECURITY.md`, and an **agent production-install guide** (outbound firewall requirements, service/systemd install, upgrade).

---

## 2. Audit addendum (paste after the finish-plan Common preamble)

```text
AUDIT CONTEXT (2026-07-13 production-readiness audit — see docs/production-readiness-audit-2026-07-13.md):
- This task fixes a specific audited defect. The finish-plan (WP1-15) and the db-schema-hardening
  chain are MERGED; line numbers in the audit have shifted — locate every anchor by SYMBOL name.
- Pre-production policy still holds: breaking changes to wire contracts / EF schema / REST / step
  names ARE allowed. Prefer the clean fix over a back-compat shim. No "soft-fallback for old data"
  branches — a violated invariant should throw, not paper over.
- When a fix changes a gRPC .proto, a SignalR hub interface, or an EF schema, say so explicitly in
  the PR description under a "CONTRACT CHANGE" heading — these freeze at v1 and reviewers must see them.
- Do not expand scope into other WPs. If you find an adjacent defect, note it in the PR, don't fix it.
- Sensitive data: never log secrets, connection strings, internal IPs, or AD structure. Sanitise
  examples. This product runs at RH state institutions under GDPR.
```

---

## 3. Phase A — Security fast-fixes (do first; A1/A2/A3/A5 are hours, not days)

### A1 — Package-upload path sanitization (T0-5)

```text
TASK: Close an authenticated arbitrary-file-write → RCE. PackageService.UploadAsync validates
packageId/version for path separators and ".." but never validates the uploaded fileName;
LocalPackageStore.StoreAsync does Path.Combine(dir, fileName) then FileMode.Create. The fileName
is attacker-controlled multipart Content-Disposition from POST /api/packages/upload, gated only by
Permission.PackageEdit. A file named "..\..\..\wwwroot\shell.aspx" (or an absolute rooted path)
writes outside the package tree → web shell / binary overwrite → code execution as the service
account (which holds the agent JWT signing key). LocalArtifactStore already sanitises; packages do not.

Scope:
1. In PackageService.UploadAsync: reduce fileName to Path.GetFileName(fileName); reject (400) if it
   changed, is empty, or still contains a separator / "..". Apply the SAME guard packageId/version
   already get. Mirror LocalArtifactStore.SanitiseName for consistency.
2. Defence in depth in LocalPackageStore: after building the full path in StoreAsync / GetFullPath /
   DeleteAsync, assert Path.GetFullPath(result) starts with Path.GetFullPath(RootPath) + separator;
   throw otherwise. Covers the multi-account "accounts/{id}/packages" root too.
3. Grep for any other endpoint that Path.Combines a user-supplied filename (artifact upload is fine;
   check offline-drop ingest, step-package upload) and confirm each is guarded.

Acceptance: a unit test uploads with fileName "../../evil.txt" and an absolute path and asserts both
are rejected and nothing is written outside RootPath; a normal upload still works; existing package
tests green. CONTRACT CHANGE: none (behaviour-only).
Branch: fix/sec-package-path-traversal
```

### A2 — Secret masking in task logs + sensitivity plumbing (T0-6, and output-variable sensitivity)

```text
TASK: Sensitive variables are encrypted at rest but become UNMARKED plaintext once resolved into a
deployment — they ride the plan, become step-process env vars and the server-side %TEMP% PowerShell
preamble, and any script that echoes one persists it verbatim into task_step_logs (queryable, shown
on the log tab, downloadable). Octopus masks these; we don't. Root cause: DeploymentPlan.Variables
(src/KrakenDeploy.Contracts/DeploymentContracts.cs) is a flat Dictionary<string,string> with no
per-variable sensitivity flag. This is the most likely GDPR/state-sector compliance blocker.

Scope:
1. Carry sensitivity onto the plan: add a set of sensitive variable NAMES (or a
   Dictionary<string,VariableFlags>) to DeploymentPlan. VariableService already knows which variables
   are sensitive (it encrypts them / redacts them in ToDto) — populate the set at plan-build time in
   the worker for BOTH deployments and runbook runs, and for the offline drop plan.
   [CONTRACT CHANGE: DeploymentPlan gains a field — note it.]
2. Mask on OUTPUT (not input — scripts still need real values): wrap the agent's log callback in a
   redactor that replaces every exact sensitive VALUE substring with "***" before it reaches
   IServerLink.AppendLogAsync. Apply the same redaction in ServerScriptStepRunner's stdout/stderr
   pump (server-side steps). Redact known secret values, not by name-matching the log text.
3. Output variables (M5): TaskOutputVariable.Value is plaintext and rendered raw in
   DeploymentDetail.razor and fed into later steps. Support a sensitive output marker
   (Set-OctopusVariable -sensitive), encrypt those rows via the existing DEK path, mask them in the
   UI, and add their names to the plan's sensitive set so downstream steps' logs mask them too.
4. Confirm the AI diagnosis path (DiagnosisContextAssembler) — it already decrypts snapshot secrets to
   redact them; make sure it uses the same sensitive-name source so it stays in sync.

Acceptance: a deployment with a sensitive variable whose script does `Write-Host $secret` shows "***"
in the live log, the persisted task_step_logs, and the downloaded log; a sensitive output variable is
encrypted at rest and masked in the UI; non-sensitive values are untouched. New tests cover the
redactor with overlapping/substring values. CONTRACT CHANGE: DeploymentPlan sensitivity field.
Branch: fix/sec-log-secret-masking
```

### A3 — Remove agent role self-assignment (T1-7)

```text
TASK: A registering agent can self-declare its authorization roles. AgentHub.RegisterAsync does
`if (request.Roles.Count > 0) target.Roles = request.Roles.ToList();`. Roles drive secret scoping
(VariableScope.Matches grants any variable whose scope roles intersect the target's roles, resolved
at dispatch with the target's CURRENT roles). A compromised agent for a low-trust box registers with
Roles=["db","prod-secrets"] and receives those scoped secrets in its next deployment. Octopus assigns
Tentacle roles operator-side; they are never self-declared.

Scope:
1. Delete the Roles assignment from RegisterAsync. Roles are set only via the registration wizard /
   target-edit UI / API by an operator with the right permission. Confirm those operator paths exist
   and are the sole writers of DeploymentTarget.Roles.
2. If the agent legitimately reports machine capabilities (OS, arch, installed tooling), keep that in
   SEPARATE fields (MachineName/OperatingSystem/AgentVersion already exist) that never feed
   VariableScope or step-role matching. Do not let any agent-supplied field influence scoping.
3. Audit-log a warning if a registration request arrives WITH a non-empty Roles list (signals a
   tampered/old agent) — but ignore the value.

Acceptance: registering an agent with a Roles payload does not change the target's roles; an operator
setting roles in the UI still works; a test asserts RegisterAsync ignores request.Roles. CONTRACT
CHANGE: AgentRegistrationRequest.Roles becomes informational/ignored — note it (consider removing the
field in B6's contract pass).
Branch: fix/sec-agent-role-selfassign
```

### A4 — Enforce sub-Space RBAC on the execution surface (T1-8)

> **Status: ✅ DONE — verified + completed in code 2026-07-16.** (Originally found Partial; the residual gaps were then closed.)
>
> **Mechanism:**
> - `RoleAssignmentScopeMatcher` **strict mode** — a dimension the grant restricts but the caller left `null` fails closed for writes (`return !strict`); an empty grant list (Space-wide) still auto-passes.
> - `PermissionEvaluatorExtensions.EnsureScopedAsync(caller, perm, scope)` — the authoritative service-layer check (`bypassCache:true, strictScope:true`, throws `AuthorizationException`); `CallerAuthorization` (`ForUser`/`System`, no fail-open default).
>
> **Enforced with a resolved scope across the full mutating/execute surface** (all thread `CallerAuthorization`; REST maps `AuthorizationException` → 403; Blazor handlers degrade gracefully; system paths pass `CallerAuthorization.System`):
> - Deployments: `CreateAsync` (Test→Prod closed, before any info leak), `CancelAsync` (`TaskCancel` @ project/env/tenant), `OfflineDropBundleBuilder.RegenerateForDeploymentAsync` (`DeploymentCreate`).
> - Releases: `CreateAsync`, `UpdateVariablesAsync` (`ReleaseEdit` @ project).
> - Runbooks: `CreateAsync`/`UpdateAsync`/`DeleteAsync` + step edits (`RunbookEdit`), `TriggerAsync` (`RunbookRunCreate`), `CancelRunAsync` (`TaskCancel`).
> - Process/Variable edits (`ProcessEdit`/`VariableEdit`), `TenantService` tenant-keyed mutations.
>
> **Tests:** `tests/KrakenDeploy.Server.Data.Tests/SubSpaceRbacExecuteTests.cs` (Test→Prod deploy + regenerate + cancel reject, runbook-run scope, cross-project process/variable/release/runbook edit reject + allow, system-bypass) + `tests/KrakenDeploy.Server.Core.Tests/RoleAssignmentScopeMatcherTests.cs`. Adversarial 2-lens review (bypass-hunt + scope-correctness) → the regenerate gap + the 403-vs-500 contract were the only findings and both fixed.
>
> **Deliberately out of scope (low/design, not exploitable):** `ProcessService.GetOrCreateAsync` / `VariableService.GetOrCreateSetAsync` create only an empty container (downstream mutators are scoped); `TenantService.ConnectProjectAsync`/`DisconnectProjectAsync` authorize the Tenant dimension only (deploys remain gated at project+env+tenant) — intentional.

```text
TASK: Sub-Space (Project/Environment/Tenant) role scoping is not enforced at execution time.
RoleAssignmentScopeMatcher.DimensionMatches treats a null scope dimension as an OPTIMISTIC match; the
minimal-API endpoints call .RequirePermission(...) with no PermissionScope resource; the mutating
services (DeploymentService.CreateAsync, ProcessService, ReleaseService, VariableService) take no
principal and re-check nothing. Only 1 of 123 Guard.AllowAsync call sites passes a scope
(DeployReleaseDialog, which proves the intended model). Result: a user granted "DeploymentCreate on
Environment=Test" can deploy to Prod via POST /api/deployments; a "ProcessEdit on Project A" user can
edit Project B. Affects REST, CLI, and MCP.

Scope:
1. Add a strict matcher mode: for WRITE/execute permissions, a null scope dimension must NOT auto-pass
   — the caller must supply the concrete Project/Environment/Tenant. Keep optimistic-null only for
   broad read checks where that's intended (audit the read call sites before changing global behaviour).
2. Thread scope into the enforcement points. Provide a RequirePermissionScoped helper (or resolve the
   target entity's Project/Environment/Tenant in the endpoint and set context.Resource to a full
   PermissionScope) for the mutating minimal-API endpoints: deployments create, releases create,
   process step edits, variable edits, runbook run, tenant ops. Mirror DeployReleaseDialog.
3. Belt-and-braces in the services: have the mutating service methods take the principal (or an
   authorization delegate) and re-check scope against the resolved entity, so CLI/MCP callers that
   bypass the endpoint are still enforced. This is the authoritative layer.
4. MCP write tools: confirm they route through the same service-layer check (see A5 for reads).

Acceptance: a user scoped to Environment=Test is REJECTED (403) creating a deployment to a Prod target
via the API, the CLI, and MCP; a Project-A-scoped ProcessEdit user cannot edit Project B; the existing
UI dialog path is unchanged; broad-read behaviour is unchanged. Tests cover the Test→Prod bypass and
the cross-project edit for all three surfaces. CONTRACT CHANGE: authorization semantics of the null
scope dimension for writes — document loudly (it's a deliberate break of the current optimistic rule).
Branch: fix/sec-subspace-rbac-enforcement
```

### A5 — MCP read-tool authorization (T1-9)

```text
TASK: MCP read tools/resources authenticate but perform NO permission check. Only retry_deployment
calls McpToolAuth.EnsureAsync; get_deployment_log, get_step_config ("complete, unredacted config"),
list_failed_deployments, get_deployment_diff, DeploymentLogResource, TargetTools, ReleaseTools do not.
The MCP transport is .RequireAuthorization() only. So any authenticated API key (whose owner may hold
zero relevant permissions) reads full deployment logs (which per A2 may contain plaintext secrets until
that lands), step configs, and target/release data that the REST equivalents gate on
DeploymentView/ProcessView/MachineView/ReleaseView. Space scoping still holds (global filter), so this
is within-Space over-exposure, not cross-Space.

Scope:
1. Add McpToolAuth.EnsureAsync(..., <the matching View permission>, ...) to every read tool and
   resource, mirroring retry_deployment: deployment logs/diff/list → DeploymentView; step config →
   ProcessView; targets → MachineView; releases → ReleaseView. Keep the (account, space) keying.
2. Audit the full MCP tool/resource inventory (DeploymentTools, TargetTools, ReleaseTools, resources)
   so none are missed; add a test that enumerates registered MCP tools and asserts each has an auth
   gate (fail-closed default).

Acceptance: an API key whose owner lacks DeploymentView gets denied on get_deployment_log via MCP but
a key with it succeeds; a test proves every MCP tool/resource calls the auth gate. CONTRACT CHANGE: none.
Branch: fix/sec-mcp-read-authz
```

### A6 — SSRF hardening (T1-11)

```text
TASK: The SSRF guard is under-applied and bypassable. SsrfGuard validates only the INITIAL webhook URL;
the HttpClient is registered with default AllowAutoRedirect=true, so a webhook target returning
302 Location: http://169.254.169.254/... is followed unvalidated; non-2xx bodies are echoed (512 chars)
into delivery history (readable SSRF). RFC1918 is allowed by design. Catalog download, GitHub catalog,
OIDC Authority discovery, and the AI endpoint fetch are not guarded at all. On a segmented gov network
the internal-network-probe vector is real.

Scope:
1. Webhook: set AllowAutoRedirect=false on the primary handler (treat 3xx as delivery failure), OR pin
   the validated IP via SocketsHttpHandler.ConnectCallback and re-run the guard per hop. Stop echoing
   downstream response bodies into delivery history (store status code + a fixed message).
2. Route the catalog (StepPackageCatalogService, GitHub), OIDC metadata (PerAccountOidcConfigureOptions),
   and AI endpoint (KrakenAiClientFactory) fetches through SsrfGuard at use/save time. These are
   admin-configured (lower risk) but must not be an unfiltered outbound primitive.
3. Add a default-deny-RFC1918 mode with an explicit per-integration operator allowlist (config). Default
   posture for on-prem gov = deny RFC1918 unless allowlisted. Pin the validated IP to close DNS-rebind
   TOCTOU where feasible.

Acceptance: a webhook to a redirector pointing at 169.254.169.254 fails without following; a webhook to
an RFC1918 host is denied unless allowlisted; response bodies are no longer echoed; catalog/OIDC/AI URLs
are guarded. Tests cover the redirect bypass and the RFC1918 allow/deny. CONTRACT CHANGE: none (config
adds an allowlist key — document it).
Branch: fix/sec-ssrf-hardening
```

### A7 — Auth-session hardening (T1-13, T1-14, M2)

```text
TASK: Batch of session/cookie hardening for a credentials product.
1. No session revocation (T1-13): AddIdentityCore + hand-rolled cookie, no SecurityStampValidator, no
   revalidating auth-state provider, no user-disable flag, 7-day sliding cookie. A password reset or
   offboard cannot terminate an existing session/circuit.
   - Wire SecurityStampValidator into the cookie with a short ValidationInterval; add a
     RevalidatingServerAuthenticationStateProvider for the Blazor circuit; bump the security stamp on
     password change / role change / disable; add an explicit IsDisabled flag on the user checked at
     sign-in and revalidation. (Note: RBAC is live-resolved, so this closes AUTHENTICATION, not just
     authorization.)
2. DataProtection ring unencrypted on Linux/HA (T1-14): ProtectKeysWithDpapi is Windows-only; on
   Linux/HA the shared key dir relies on volume perms only — read it and you forge auth+antiforgery
   cookies. Configure ProtectKeysWithCertificate (cert from config/KMS) for non-Windows/HA; document
   required directory ACLs. Ensure the ring path is under Server:DataPath (see C2) so backup captures it.
3. Cookie Secure + forwarded headers (M2): SecurePolicy=SameAsRequest with no UseForwardedHeaders means
   behind the YARP Router (TLS-terminating) the app sees HTTP and drops the Secure attribute. Set
   CookieSecurePolicy.Always outside Development and configure ForwardedHeaders (known proxies/networks)
   so Request.IsHttps / RemoteIpAddress are correct (also sharpens the agent-register rate-limit partition).

Acceptance: changing a user's password invalidates their other active sessions within the validation
interval; a disabled user's circuit is terminated on next revalidation; behind a TLS proxy the auth
cookie carries Secure; DP keys are encrypted at rest on Linux. Tests where feasible; document the cert/KMS
setup. CONTRACT CHANGE: none (adds IsDisabled column — migration).
Branch: fix/sec-auth-session-hardening
```

### A8 — Agent transport auth hardening (T1-12, T1-15)

```text
TASK: Harden the agent auth + offline trust boundary.
1. Agent JWT (T1-12): AgentJwtService issues a 365-day HS256 token delivered via ?access_token= query
   string; Program.cs has ValidateIssuer=false/ValidateAudience=false; the only revocation is deleting
   the target; agent.json is plaintext with default ACLs on Windows (chmod-600 Unix only).
   - Add a TokenVersion (or security-stamp) claim compared to a column on the target row so a token can
     be revoked without deleting the target; shorten lifetime and add an agent-side refresh path; flip
     ValidateIssuer/ValidateAudience on (iss/aud are already stamped) after re-enroll; protect agent.json
     on Windows via ProtectedData (DPAPI) or a restrictive ACL. Redact access_token in request logging.
   - Cleartext H2: Http2UnencryptedSupport is set unconditionally, process-wide, in all three gRPC
     clients. Gate it behind a single Development-only flag; refuse a non-https server URL outside
     Development (explicit dev override only).
2. Offline unsigned acceptance (T1-15): OfflineResultService skips HMAC + signature verification when no
   per-target key is configured, then trusts the bundle to drive DB writes (success, step outcomes,
   output vars). Require a key for offline-drop targets — fail closed if a result bundle arrives for a
   target with no key. Keep the correct keyed path (FixedTimeEquals + format guard) unchanged.

Acceptance: an old/forged token failing the version claim is rejected; a non-https agent URL is refused
outside Dev; an unsigned offline result for a keyed-required target is rejected; agent.json is
ACL/DPAPI-protected on Windows. Note the enrollment/PoP contract is reserved in B6. CONTRACT CHANGE:
AgentJwt claims + validation; note it.
Branch: fix/sec-agent-transport-auth
```

---

## 4. Phase B — Engine resilience (the core bet)

### B1 — Durable dispatch + startup reconciler + atomic claim (T0-1, T1-2)

```text
TASK: The dispatch queue is a non-durable in-process Channel<TenantWorkItem>. A server crash/restart
between the Queued insert and channel consumption strands the deployment in Queued FOREVER (the only
re-scan job filters ScheduledFor != null); a crash mid-run strands it in Running. Runbook runs share
the flaw. Separately, there is no atomic claim: the worker Include-loads then blind-writes Running, and
CreateAsync with a past scheduledFor BOTH enqueues immediately AND persists ScheduledFor, so the
minutely dispatch job re-enqueues during the worker's prep window → the plan executes twice.

Scope:
1. Startup reconciler (hosted service, runs once on boot, per account in M-A mode): re-enqueue orphaned
   Queued rows (ScheduledFor null) and mark pre-crash Running rows as Interrupted/Failed with an audit
   row + terminal status (they cannot be resumed — the TCS registry was in-memory). Emit clear audit +
   log lines. Idempotent (safe to run every boot).
2. Atomic claim: change the worker's pickup to a conditional UPDATE ... SET status='Running'
   WHERE id=@id AND status='Queued' RETURNING ...; skip if 0 rows (already claimed/cancelled). This
   also fixes the double-dispatch race.
3. Fix CreateAsync scheduling: a deployment with a due/past ScheduledFor should take exactly ONE path —
   either enqueue immediately and clear ScheduledFor, or leave it for the dispatch job. Not both. The
   dispatch job's own claim must recheck ScheduledFor IS NOT NULL inside the UPDATE.
4. Decide durability posture and STATE it: either (a) keep the in-process Channel but make the DB the
   source of truth with the reconciler as the safety net, or (b) route immediate dispatch through
   Hangfire (already in-process and durable) like scheduled dispatch. Recommend (b) for a deployment
   tool — a Queued row must never depend on process memory to run.

Acceptance: kill the server with a Queued and a Running deployment; on restart the Queued one runs and
the Running one is terminal+audited (not stuck); a deployment with a past ScheduledFor executes exactly
once (test the enqueue+job race). Orchestrator harness tests cover claim + reconcile. CONTRACT CHANGE:
none (internal). Touches DeploymentService, DeploymentWorker, ScheduledDeploymentDispatchJob, new
StartupReconciler.
Branch: fix/eng-durable-dispatch
```

### B2 — Agent reconnect: unbounded + supervised (T0-2)

```text
TASK: The agent abandons the tunnel after ~40s and never reconnects. SignalRServerLink uses bare
.WithAutomaticReconnect() (retries [0,2,10,30]s then closes permanently); ServerLinkHostedService parks
on Task.Delay(Infinite); the Closed handler only logs. So any server restart/deploy/blip > ~40s takes
the agent offline until its PROCESS is manually restarted — and combined with blue-green, a
zero-downtime server upgrade knocks the entire agent fleet permanently offline.

Scope:
1. Pass a custom IRetryPolicy to WithAutomaticReconnect with unbounded, jittered, capped backoff
   (e.g. cap at 30-60s, forever). The connection must keep trying for the life of the process.
2. Supervise Closed in ServerLinkHostedService: on a permanent close (policy exhausted or explicit
   close that isn't shutdown), loop StartAsync with backoff until stoppingToken — the hosted service
   must never sit idle with a dead connection. Distinguish clean shutdown (stop trying) from failure.
3. On Reconnected/Reconnect: re-send the blue-green release pin header (already persisted in options),
   re-assert Online, and re-send any unreported step/deployment completions the agent buffered while
   disconnected (coordinate with B3 — the server may have reaped the wave; a late completion must be
   handled, not lost).
4. HeartbeatHostedService: ensure it drives/observes the reconnect rather than spinning on
   IsConnected==false.

Acceptance: stop the server for 5 minutes with an idle agent, restart it → the agent reconnects
automatically and shows Online without a process restart; do the same during a blue-green server slot
swap → the fleet stays connected. Test the retry policy is unbounded. CONTRACT CHANGE: none.
Branch: fix/eng-agent-reconnect
```

### B3 — Disconnect reconciliation + server-side wave deadline (T0-3)

```text
TASK: A deployment strands in Running forever when an agent drops mid-wave with the default step config.
DispatchTargetWaveAsync arms CancelAfter only when TimeoutSeconds > 0, but the default is 0 (unlimited),
so it awaits the TaskCompletionSource with no deadline; AgentHub.OnDisconnectedAsync removes the
connection and schedules an offline mark but NEVER cancels pending sub-plan slots. Second-order: the
leaked dispatch keeps InFlightWorkGauge > 0, and ReleaseDrainDecision.ShouldRetire treats in-flight as
"never retire" → one dead agent blocks blue-green retirement indefinitely.

Scope:
1. On AgentHub.OnDisconnectedAsync (after the reconnect grace so a fast blip doesn't kill a live wave —
   coordinate the grace with B2), cancel every open PendingSubPlanRegistry slot for that target
   (subPlans.Cancel(taskId, targetId, "agent disconnected")). The worker's await then throws/returns and
   the deployment resolves per its failure mode instead of hanging.
2. Enforce a server-side MAXIMUM wave/dispatch deadline independent of the per-step TimeoutSeconds, so
   the "await agent forever" path cannot exist even at TimeoutSeconds=0. Make the default configurable
   with a sane non-zero ceiling. A late CompleteDeploymentAsync after the deadline must be handled
   idempotently (see B5's cancelled/terminal guard).
3. Verify InFlightWorkGauge is decremented on this cancellation path so blue-green drain can proceed;
   add a test that a dead agent no longer blocks ShouldRetire past the deadline.
4. Do the same for runbook runs (RunbookRunWorker) — today they have no completion timeout at all and no
   cancel API (this partly overlaps D1; if D1 lands first, ensure the merged orchestrator covers it).

Acceptance: start a deployment, kill the agent mid-step with default (0) timeout → after the grace the
deployment goes terminal (Failed/BestEffort per mode), not stuck Running; InFlightWorkGauge returns to
0 and blue-green retirement completes; a late completion after reap doesn't corrupt status. Orchestrator
tests cover disconnect-mid-wave. CONTRACT CHANGE: none.
Branch: fix/eng-disconnect-reconciliation
```

### B4 — Online cross-step output variables + server-side capture (T0-4, T1-6)

```text
TASK: Output variables silently do NOT propagate between steps on the online deployment path. With the
default StartAfterPrevious trigger each step is its own wave; the target dispatch context (incl.
Plan.Variables) is built ONCE before any step runs, and each wave sends the same static bag. Captured
outputs are persisted to TaskOutputVariables and drained for collision/attribution but never merged back
into a later wave's sub-plan; the agent's AugmentPlanWithPriorOutputs only accumulates within a single
ExecuteAsync (one wave online), so it always starts empty. So #{Octopus.Action[Step1].Output.Url} in
step 2 resolves to empty. Offline drops and runbooks work (whole plan dispatched once) — this is an
online-split regression. Separately, server-side steps (ServerScriptStepRunner) never capture outputs at
all (no OctopusMessageParser interceptor).

Scope:
1. Before each subsequent wave dispatch, merge Octopus.Action[<accumulatorKey>].Output.* (from the
   PendingSubPlanRegistry perStepResults or a re-read of TaskOutputVariables for this task) into that
   wave's sub-plan Variables, then re-run the Octostache substitution for that wave's step configs so
   #{...Output...} references resolve. OR coalesce consecutive same-side steps into one sub-plan so the
   agent's accumulator spans them. Pick one; the merge approach is more robust for mixed server/target
   sequences.
2. Server-side capture (T1-6): extract the agent's OctopusMessageParser output-variable handling into a
   shared utility (KrakenDeploy.Execution or a shared helper) and apply it in ServerScriptStepRunner so
   RunOnServer steps capture Set-OctopusVariable outputs the same way, feeding the same
   ReportStepOutputVariables path.
3. Verify cross-iteration (ForEach) output access still works (synthetic accumulator keys) after the
   change — CrossIterationOutputResolutionTests must stay green.

Acceptance: a 2-step online deployment where step 1 sets an output and step 2 reads
#{Octopus.Action[Step1].Output.X} sees the real value (agent AND server-side step variants); ForEach
cross-iteration output tests green; offline/runbook parity unchanged. New orchestrator test for the
online multi-wave output hand-off. CONTRACT CHANGE: none (internal plan reshaping).
Branch: fix/eng-online-output-vars
```

### B5 — Optimistic concurrency + cancel-guard on all status writers (T1-1, T1-5)

```text
TASK: All ServerTask status writers are blind last-writer-wins and can corrupt terminal state.
Specifically, AgentHub's fallback completion path (when subPlans.TryResolve returns false) blindly writes
Succeeded/Failed with NO Cancelled guard (unlike the worker's finalize and FailAsync, which correctly
re-read status) — reachable after a wave timeout, a server restart, or a duplicate completion — so a
Cancelled deployment flips back to Succeeded/Failed, and it fires log-compaction + retention mid-flight.
ServerTask carries no concurrency token, so CancelAsync (read-check-write), worker finalize, FailAsync,
and the hub fallback all race.

Scope:
1. Add an xmin optimistic-concurrency token to ServerTask (Npgsql: .UseXminAsConcurrencyToken() /
   IsRowVersion mapping) OR make every terminal write a status-guarded conditional UPDATE
   (... WHERE id=@id AND status NOT IN (terminal states)). Prefer xmin — it protects all writers at once.
2. Add the Cancelled/terminal guard to AgentHub's fallback completion (re-read authoritative status;
   never overwrite a terminal state; do not run retention/compaction for a task that's already terminal).
3. Ensure the fallback path is only reached when genuinely no slot is open, and that a late completion is
   a no-op against a terminal row rather than a corrupting write. Coordinate with B3's post-deadline late
   completion.
4. Audit CancelAsync, worker finalize, FailAsync, and RunbookRunWorker.FailAsync (which writes Failed
   without checking existing terminal state) — all must respect the guard/token.

Acceptance: a race test (cancel + late agent completion) leaves the deployment Cancelled, not
Succeeded/Failed; retention/compaction never runs twice or against a terminal task; concurrent writers
get a concurrency exception handled gracefully (re-read + no-op if terminal). CONTRACT CHANGE: none
(adds xmin/rowversion mapping — migration, no data change).
Branch: fix/eng-status-concurrency
```

### B6 — Agent abort + attempt-idempotency wire contract (T2-5, T1-3) — NOW-OR-NEVER

```text
TASK: Extend the agent wire contract while it's still unfrozen, to enable cooperative cancellation and
de-duplication of re-dispatched work. Today: there is NO server→agent abort (IAgentHubClient exposes
only RunDeploymentAsync/RunAdhocScript), so CancelAsync flips the DB to Cancelled while the agent runs
to completion; the agent never kills the child process on cancel; and completion is keyed only by
(deploymentId, targetId), so a re-dispatched wave (retry/timeout) is indistinguishable from the original
and a stale completion resolves the new attempt's TCS.

Scope (CONTRACT CHANGES — call them out in the PR):
1. Add CancelDeploymentAsync(Guid taskId) (and a runbook equivalent, or a unified taskId if D1 landed)
   to IAgentHubClient. Agent side: keep a ConcurrentDictionary<Guid, CancellationTokenSource> of running
   tasks; on cancel, signal the CTS and Kill the process tree in ScriptRunner
   (process.Kill(entireProcessTree:true)) — today WaitForExitAsync(ct) never kills the child, leaking
   orphan processes on cancel/timeout.
2. Add an AttemptId/DispatchId (Guid) to DeploymentPlan, echoed back in CompleteDeploymentAsync /
   ReportStepCompletedAsync / AppendLogAsync. The server correlates a completion to the attempt that
   produced it; the agent refuses/ignores a duplicate in-flight taskId (idempotency). PendingSubPlanRegistry
   keys slots per attempt so a stale completion can't resolve a new attempt (fixes T1-3's core).
3. Add an integer ContractVersion to AgentRegistrationRequest; the server refuses/quarantines an
   incompatible agent at connect (today stepIndex was added to AppendLogAsync with no negotiation, so an
   old agent silently drops all log/step reports). Bounds-check agent-supplied stepIndex at the hub
   boundary (int.MaxValue → array index throw → cross-target deployment abort).
4. Reserve (define but may leave unimplemented) the enrollment/PoP contract shapes from
   docs/design-agent-enrollment-cert-auth.md — the enroll endpoint shape, a connect-time nonce challenge
   field, and a gRPC DPoP header — so cert auth can ship post-v1 without a frozen-contract retrofit.
5. Decide kraken.proto resume_offset: implement it on the client (stat the temp file, request from its
   length, append) OR remove it from the contract so it isn't a permanent false promise.

Acceptance: cancelling a running deployment actually stops the agent's process tree within seconds and
the status is Cancelled; a re-dispatched wave's stale completion does not resolve the new attempt; an
agent reporting a ContractVersion mismatch is refused with a clear message; out-of-range stepIndex is
rejected at the boundary. CONTRACT CHANGE: IAgentHubClient +CancelDeploymentAsync; DeploymentPlan
+AttemptId; AgentRegistrationRequest +ContractVersion; reserved enroll/PoP fields; resume_offset
resolved. This is the big pre-freeze wire pass — do it before external agents exist.
Branch: fix/eng-agent-abort-idempotency-contract
```

### B7 — Node concurrency cap + safe package cache + retry re-resolve (T1-3, T1-4)

```text
TASK: There is zero node-level concurrency control and the package cache is unsafe under concurrency.
DeploymentWorker fire-and-forgets every work item; the agent runs any number of plans concurrently
(Task.Run per push); DeploymentExecutor.IsExecuting is a racy single bool that the self-updater reads to
decide it's safe to swap the binary. LocalPackageCache.TryGetCachedPath can return a path a concurrent
StoreAsync (FileMode.Create truncation) is half-writing → truncated-zip extraction / IOException; no
temp-file+rename, no checksum. Also H2: the wave retry reuses a connectionId resolved once per batch, but
SignalR assigns a new id on full reconnect, so retries silently no-op to a dead id.

Scope:
1. Bounded worker parallelism: add a configurable node task cap (Octopus defaults to 5) via a
   SemaphoreSlim/bounded scheduler in the worker; queue excess. Add per-target single-flight so two
   deployments to the same target don't interleave (server-side queue keyed by target, or an agent-side
   execution queue). Replace IsExecuting with a running-task registry (reuse B6's dictionary) — a counter,
   not a bool — and have the self-updater consult it.
2. Safe package cache: LocalPackageCache.StoreAsync writes to a temp file then atomically renames into
   place; TryGetCachedPath verifies a checksum (or a completion marker) before returning a hit; concurrent
   stores of the same package are single-flighted. Same review for the step-package cache.
3. Retry connection re-resolve (H2): re-resolve registry.GetConnectionId(targetId) at the top of each
   retry attempt; treat "no connection" as a fast retry-eligible condition, not a silent send-to-void.

Acceptance: N concurrent deployments respect the task cap; two deployments to one target serialize; a
concurrent cache store+read never yields a truncated package (stress test); a retry after the agent
reconnects reaches the new connection id. Tests cover the cap, the cache race, and the reconnect-retry.
CONTRACT CHANGE: none (config adds a task-cap key).
Branch: fix/eng-concurrency-and-cache
```

### B8 — Server↔agent transport round-trip integration test (test gap)

```text
TASK: There is NO test that drives a real DeploymentPlan over real SignalR to a real agent and back —
every leg is green in isolation, so a serialization/contract drift at the seam (very likely after B6's
wire changes) passes 100% of CI. Close this before the v1 freeze.

Scope:
1. Add an integration test (Testcontainers Postgres + an in-process ASP.NET TestServer hosting the real
   hubs + a real in-process Agent Worker/DeploymentExecutor connected over the loopback SignalR
   connection — NOT the FakeAgentHubContext). Create a release with a trivial script step, trigger a
   deployment, and assert: Status=Succeeded, a log line arrived, and a step-1 output variable is read by
   step 2 (guards B4). Add a second case with a server-side (RunOnServer) step.
2. Extend the on-prem smoke script (smoke-test.sh) to actually TRIGGER a deployment and assert success,
   and run the single-instance smoke on PRs (today smoke runs only on push-to-main and asserts only
   connectivity).
3. Add a failure-seam case: agent disconnect mid-deployment asserts terminal (not stuck) — guards B3.

Acceptance: the round-trip test runs in CI (Linux leg) and fails if the plan serialization or a hub
contract drifts; the PR smoke asserts a real deployment succeeds. CONTRACT CHANGE: none.
Branch: test/eng-transport-roundtrip
```

---

## 5. Phase C — Packaging & ops

### C1 — Backup/restore image + round-trip CI (T0-7)

```text
TASK: In-container backup AND restore are non-functional — the runtime image (Dockerfile.server) installs
only curl, but BackupCommands, the nightly BackupEngine, and RestoreCommands all shell out to pg_dump/psql.
The recommended on-prem deployment cannot back up or restore. This is the most dangerous ops finding: the
DR mechanism is broken in the deployment we recommend.

Scope:
1. Add postgresql-client to the runtime stage of Dockerfile.server (and any other image that runs
   backup/restore) — match the client major to the Postgres server major (16). Keep the image lean
   (--no-install-recommends). Alternatively ship a dedicated backup sidecar; the client-in-image path is
   simpler for on-prem.
2. Add CI that runs a REAL backup→restore round-trip against the on-prem image: seed a DB, `backup`,
   restore into a fresh stack, assert login works AND a decrypted secret is readable (requires the KEK to
   be provided — see C2). This is the acceptance gate for DR.
3. Verify FindPgDump/psql discovery finds the tools at their apt install path inside the container.

Acceptance: `docker compose exec kraken-server ... backup` produces a bundle; restore into a fresh stack
yields a working login + a decrypted secret; the CI round-trip is green. CONTRACT CHANGE: none.
Branch: fix/ops-backup-image
```

### C2 — On-prem compose: DEK init + DataPath unify (T0-8, T0-9)

```text
TASK: Two coupled on-prem packaging defects that brick a fresh install.
1. DEK brick (T0-8): the kraken-init service (runs `database setup`) sets neither DOTNET_ENVIRONMENT nor
   Encryption__MasterKey, so DatabaseCommands defaults to Development and takes the ephemeral-KEK branch —
   it wraps the DEK under a random key. kraken-server then boots Production with the real ENCRYPTION_KEY
   and never re-provisions → first sensitive-variable access throws CryptographicException (GCM tag
   mismatch). Fix: add Encryption__MasterKey: ${ENCRYPTION_KEY} AND DOTNET_ENVIRONMENT: Production to
   kraken-init (the latter turns the silent brick into a clean fail-fast if the key is missing).
2. DataPath (T0-9): compose sets Server__DataPath: /data and mounts kraken-data:/data (root-owned), but
   the image only chowns /var/lib/krakendeploy and runs USER kraken → the non-root process can't write.
   AND six code sites read bare "DataPath" (Program.cs DataProtection ring + drop-bundle download,
   DeploymentWorker, StepPackageService, GrpcStepPackageDeliveryService, OfflineDropBundleBuilder) vs
   "Server:DataPath" elsewhere → DP ring / step-packages / offline bundles land in a different,
   unwritable, unpersisted, un-backed-up tree.
   - Standardise ALL sites on "Server:DataPath" (grep for the bare key; fix each). Mount the volume at
     /var/lib/krakendeploy (what the Dockerfile chowns and the blue-green smoke compose already uses),
     set Server__DataPath and DataProtection__KeyPath to it.
3. Document loudly that ENCRYPTION_KEY must be preserved independently of the DB dump (an env-only KEK;
   the dump is undecryptable without it) and that it is intentionally NOT in the backup bundle.

Acceptance: a fresh `docker compose up` from deploy/onprem writes packages, the DP ring, and secrets to
the persisted, writable volume; the first sensitive-variable access works (no CryptographicException);
kill+restart preserves login (DP ring persisted); the ENCRYPTION_KEY warning is in the docs and the init
output. CONTRACT CHANGE: none (config only).
Branch: fix/ops-onprem-compose
```

### C3 — Production hardening: DI validation, Npgsql, healthz (T1-18, T1-19, P1)

```text
TASK: Production robustness gaps (assumes finish-plan WP10 already added OTel export — do NOT redo OTel).
1. DI validation (T1-18): Program.cs sets no ValidateOnBuild/ValidateScopes/ValidateOnStart, so
   scope/captive-dependency errors only surface in Development (ASP.NET's dev default) and slip to
   first-resolution runtime failures in Production. Enable
   UseDefaultServiceProvider(o => { o.ValidateOnBuild = true; o.ValidateScopes = true; }) in ALL
   environments (this codebase has had captive-dependency cascades before).
2. Npgsql (T1-19): UseNpgsql is called bare — no EnableRetryOnFailure (in-flight queries hard-fail on a
   Postgres/Patroni failover instead of retrying) and no MaxPoolSize (default 100/node; a 2-node HA pair +
   Hangfire can exceed Postgres max_connections=100). Add NpgsqlRetryingExecutionStrategy and a pool cap;
   document the PgBouncer expectation from ha-pair.md and enforce/validate it.
3. healthz depth (P1): /healthz returns ok while the DEK is bricked (C2) and the data dir is unwritable.
   Add a /health/ready that probes GetDek() (decrypt round-trip) and a data-dir write, so orchestrators
   don't route traffic to a server that can't actually serve deployments.

Acceptance: a captive-dependency/scope error fails the build/boot in Production, not at first request; a
simulated DB blip is retried rather than hard-failing a deployment; /health/ready returns unhealthy when
the DEK can't decrypt or the data dir is unwritable. CONTRACT CHANGE: none.
Branch: fix/ops-production-hardening
```

### C4 — Migration data-correctness tests + expand/contract discipline (T1-17, test gap)

> **Status: NOT DONE — deferred until AFTER migration consolidation.**
>
> **Do the migration consolidation first, then C4.** We have **no production databases
> anywhere** — nothing to upgrade in place — so there is no reason to keep the long tail
> of dev migrations. The plan is to **squash the entire migration history into a single
> baseline generated from the current code state** (a fresh, clean initial migration),
> then delete the old ones. This is still in **development**; the consolidation itself is
> **not done yet** and will be scheduled separately.
>
> Once that baseline exists, C4's data-correctness tests make sense against it (seeding
> "old-shape" rows against a soon-to-be-deleted migration graph would be wasted work), and
> the expand/contract discipline (C4.2) applies to every migration authored **after** the
> baseline. Writing these tests before consolidation would test throwaway migrations.
>
> **Already done** (do not redo): the DEK-rotation completeness reflection test from C4.1
> exists — `tests/KrakenDeploy.Server.Data.Tests/DekRotationCompletenessTests.cs` (landed
> with the db-schema-hardening / C2 work). C4's remaining scope is the per-migration
> data-survival tests + the expand/contract lint, both post-consolidation.

```text
TASK (POST-CONSOLIDATION — see the Status note above; do not start until the migration
history has been squashed to a single baseline from the current code state):
Two coupled migration-safety gaps.
1. No data-correctness test (test gap): MigrationsTests proves migrations APPLY and match the snapshot,
   but nothing seeds pre-migration data and asserts it survives the destructive migrations (server_tasks
   unification, FK hardening, tag-set reset). A bad Up() silently corrupts history on first real upgrade.
   Add per-destructive-migration tests: apply up to N-1, seed old-shape rows via raw SQL, migrate to N,
   assert row counts/shape survive (for the intentionally-destructive greenfield ones, assert the
   documented drop happens and downstream invariants hold). Also add a reflection test asserting every
   *Encrypted column is covered by the DEK-rotation walk (a new encrypted store silently missed →
   permanently unreadable after the next rotation).
2. Expand/contract discipline (T1-17): self-upgrade-ha.md mandates expand/contract (no rename/drop/
   NOT-NULL in the expand phase), but the current migrations rename/drop/NOT-NULL, which faults the older
   release during a blue-green slot overlap or HA drain. Since these destructive migrations are pre-GA
   (allowed now), the action is: (a) document that all destructive schema churn must land BEFORE the v1
   baseline, and (b) add a migration-authoring checklist/CI lint that forbids rename/drop/NOT-NULL-without-
   default AND requires CREATE INDEX CONCURRENTLY / SET NOT NULL-with-validate patterns for post-baseline
   migrations (no migration currently uses CONCURRENTLY — the first index on a large real table will take
   ACCESS EXCLUSIVE and lock it).

Acceptance: a destructive migration with seeded old data has a test asserting the intended outcome; the
DEK-rotation completeness test exists; the expand/contract rules are documented and (ideally) lint-enforced
for post-baseline migrations. CONTRACT CHANGE: establishes the migration discipline for the v1 cliff.
Branch: test/ops-migration-correctness
```

### C5 — Windows/Croatian script correctness (T1-20) — LAUS-critical

> **Status: ✅ DONE — 2026-07-18.** Fixed in the two script runners
> (`steps/KrakenDeploy.Steps.Common/ScriptRunner.cs` = online/ad-hoc/offline, and
> `ServerScriptStepRunner` = server-orchestrated):
> - **Input:** `.ps1` written UTF-8 **with BOM** (`EncodingForSyntax`); bash/python stay BOM-less.
> - **Interpreter:** default → Windows PowerShell (Desktop) on Windows; explicit `Core` → pwsh; off Windows → pwsh. (Per-step Desktop/Core selector already in the UI schema.)
> - **Output** (found by adversarial review, in the acceptance's "no mojibake in output"): `StandardOutputEncoding`/`StandardErrorEncoding` = UTF-8 on both runners + `[Console]::OutputEncoding` = UTF-8 in the PowerShell preambles and the ad-hoc invoker.
> - **Artifacts:** the KrakenIis / WindowsService handlers persist their generated `.ps1` troubleshooting artifact with the same UTF-8-with-BOM.
>
> Tests: `ScriptRunnerCommandTests` + `ServerScriptStepRunnerCommandTests` (encoding + edition matrix), a Windows-only Croatian output round-trip, and a preamble assertion. Chosen fork (Domagoj): Desktop-default on Windows (Octopus parity).

```text
TASK: Two cross-platform script defects that break on Croatian-language Windows targets (directly relevant
to LAUS's own use).
1. BOM-less script files: ScriptRunner writes .ps1 via File.WriteAllText (UTF-8 WITHOUT BOM); Windows
   PowerShell 5.1 (Desktop edition) then reads the file as the system ANSI code page → any non-ASCII
   (Croatian: č, ć, š, ž, đ) in a script body, path, or message is corrupted. Write .ps1 with
   new UTF8Encoding(encoderShouldEmitUTF8Identifier: true) (UTF-8 with BOM). Verify pwsh (Core) still reads
   BOM'd files fine (it does).
2. pwsh default on stock Windows: ScriptRunner uses powershell.exe only when Edition=="Desktop"; every
   other case (including the DEFAULT, where ScriptStepHandler leaves edition null) runs pwsh — which stock
   Windows Server does not ship. A default PowerShell step fails on a fresh Windows target with "pwsh not
   found," contradicting Octopus parity. On Windows, fall back to powershell.exe when pwsh is absent (probe
   PATH), or default the edition to Desktop on Windows.

Acceptance: a script step with Croatian text in the body runs correctly under Desktop edition (no mojibake
in output or file); a default (no-edition) PowerShell step runs on a Windows box that has only Windows
PowerShell 5.1. Tests where feasible (encoding assertion + edition-resolution logic). CONTRACT CHANGE: none.
Branch: fix/ops-windows-script-encoding
```

### C6 — Agent self-upgrade atomicity + rollback (T1-21)

```text
TASK: Agent self-upgrade is a non-atomic binary swap with no rollback and no health gate. AgentUpdateService
does File.Move(current→old) then File.Copy(new→current) then Environment.Exit(0). If File.Copy throws (disk
full, AV lock, partial write) the current exe is gone and no new one is in place → the service supervisor
restarts into nothing. SHA-256 is verified only IF the server supplies it. There is no post-restart health
check and no automatic restore of *.old. A bad build or a copy failure bricks the agent — pushed fleet-wide,
the fleet.

Scope:
1. Stage → verify → atomic swap: download to a .new file, verify a MANDATORY SHA-256 (refuse the update if
   the server didn't send one), then atomically rename .new into place; keep .old (don't delete it next
   cycle until the new version is confirmed healthy).
2. Health gate + rollback: on restart, detect a failed or older-than-expected boot (write an "upgrade
   pending" marker before exit; on next start, if the new version doesn't come up healthy within a timeout,
   roll back to .old and report the failure to the server).
3. Version-skew guard: refuse to apply an update whose ContractVersion (B6) is incompatible with what the
   server advertises; report the skew rather than bricking. Ensure IsExecuting/running-task registry (B7)
   is consulted so a swap never happens mid-deployment.

Acceptance: simulate a copy failure mid-swap → the agent still boots on the old binary; a bad new build that
fails health check → automatic rollback to .old + a server-visible failure report; an update without a
server-supplied hash is refused. CONTRACT CHANGE: none (uses B6's ContractVersion).
Branch: fix/ops-agent-upgrade-atomic
```

---

## 6. Phase D — Now-or-never structural changes (before the v1 contract/schema freeze)

### D1 — Finish the server_tasks ENGINE merge (T2-1, T2-3)

```text
TASK: The server_tasks unification is DATA-MODEL-ONLY; the execution ENGINE was never merged. There are
still two orchestrators: DeploymentWorker (full: waves, server-side steps, failure modes, sub-plan
registry, conditions/retries/timeouts) and a DEGRADED RunbookRunWorker (single-target fire-and-forget: no
waves, no server steps, no StepConditionEvaluator/StepRetryRunner, no per-wave cancel, no freeze gate, no
scheduling, completion only via the hub fallback, FailAsync that doesn't even log). The domain LIES about
this: RunbookRun and ServerTask XML docs claim runbook runs get waves/failure-mode/artifacts/scheduling
"for free" and "share the orchestrator" — false today. Do this before real runbook-run history accumulates
under the degraded path (reconciling two engines' behaviours later becomes a data-migration problem).

Scope:
1. Rewrite the orchestrator to operate on ServerTask polymorphically. Resolve the process snapshot
   uniformly (today: Release.ProcessSnapshot via join for deployments vs a nullable column on server_tasks
   for runbook runs — T2-3). Settle the snapshot location NOW: either hang a ProcessSnapshotJson on
   ServerTask for both kinds, or introduce an accessor abstraction. Do it inside this merge, not after
   (moving a jsonb column post-v1 is a forward-only data migration).
2. Route runbook runs through the SAME wave partitioner, condition/required/retry/timeout evaluation,
   server/target group handling, sub-plan registry, failure-mode logic, freeze gate, and scheduling as
   deployments. Delete RunbookRunWorker (or reduce it to a thin ServerTask.Kind=RunbookRun entry point).
3. Ensure everything B1–B7 added (durable dispatch, disconnect reconciliation, wave deadline, cancel,
   idempotency, concurrency cap, status guards) applies to BOTH kinds through the single engine — runbook
   runs currently have none of it.
4. Update the ServerTask/RunbookRun XML docs to match reality (remove the "for free"/"shares the
   orchestrator" claims until they're true — which this WP makes true).

Acceptance: a runbook run with a multi-target process, a run-condition ("only if prior failed"), a step
retry, a step timeout, a RunOnServer step, and a scheduled start honours ALL of them (today it honours
none); cancel works for runbook runs; a runbook run strands neither on disconnect nor on restart (B1/B3
now cover it). The old degraded path is gone. Orchestrator tests cover runbook parity. CONTRACT CHANGE:
ServerTask snapshot location; possibly the runbook dispatch shape — note it. Sequence AFTER B1–B5.
Branch: refactor/eng-server-tasks-engine-merge
```

### D2 — Rename the Deployment→Task wire/enum surface (T2-2)

```text
TASK: Post-unification naming debt that freezes at v1. The unified spine still speaks "deployment":
DeploymentPlan, DeploymentStatus, DeploymentFailureMode, and the wire field DeploymentPlan.DeploymentId
literally carries a RunbookRun.Id for runbook runs. There are TWO status enums for one spine —
DeploymentStatus (Core/Domain/Deployments) and ServerTaskState (ServerTasksService) — bridged by a
hand-written mapping. Rename now while the gRPC/SignalR/REST contracts are unfrozen; trivial today, a wire
break post-v1.

Scope:
1. Rename the wire/DTO surface to task-neutral names: DeploymentPlan→TaskPlan, DeploymentId→TaskId,
   DeploymentFailureMode→TaskFailureMode (and the plan's step/log/complete DTOs accordingly). Update the
   .proto, the SignalR hub interfaces, the agent client, and the REST payloads.
2. Collapse DeploymentStatus + ServerTaskState into ONE TaskStatus enum; delete the hand-written mapping.
   Update all persistence, UI, API, CLI, MCP references.
3. Keep the user-facing WORD "deployment" where it's the domain concept a user deploys (a deployment IS a
   task kind) — this is about the shared spine's internal/wire names, not renaming the product concept. Use
   judgement; the goal is that the runbook-run path stops masquerading as a "deployment" on the wire.

Acceptance: builds clean; the agent and server agree on the renamed contracts; a runbook run's plan no
longer carries a field named DeploymentId holding a runbook id; one TaskStatus enum exists. Round-trip test
(B8) green against the renamed contracts. CONTRACT CHANGE: broad wire/enum rename — the point of the WP;
sequence AFTER D1.
Branch: refactor/eng-task-rename
```

### D3 — Promote control-flow config keys to typed columns (T2-4)

```text
TASK: Flags the orchestrator BRANCHES on are still stringly-typed in the jsonb Config bag, so a typo'd key
silently changes control flow with no validation: Octopus.Action.RunOnServer, Octopus.Action.MaxParallelism,
Octopus.Action.ForEach.Collection, Octopus.Action.ForEach.Parallel. The M14 knobs (Condition/Required/
retries/timeout/StartTrigger) were correctly promoted to real ProcessStep columns already — finish the job
for the control-flow flags while the jsonb→column migration is still destructively cheap.

Scope:
1. Promote RunOnServer (bool), MaxParallelism (int?), ForEach.Collection (string?), ForEach.Parallel (bool)
   to typed ProcessStep columns (or a small owned type). Migrate existing seed/import data; update the
   flattener, wave partitioner, worker, validator, importer, and both step forms to read the typed columns.
2. Keep the Octopus-compatible Config keys ONLY at the import/export boundary (map to/from columns in the
   Octopus importer/exporter) so Octopus round-tripping still works — but the engine must branch on the
   typed columns, never the raw dict.
3. Add validation: a leaf step can't carry group-only flags, etc. (extend the existing ProcessValidator).

Acceptance: a mistyped config key no longer silently changes control flow (the typed column is the source
of truth); Octopus import/export of these flags still round-trips; flattener/ForEach tests green. CONTRACT
CHANGE: EF schema (new columns) + step config shape — note it.
Branch: refactor/data-promote-controlflow-columns
```

### D4 — Split Server.Data → Server.Data + Server.Application (T2-6)

```text
TASK: Server.Data is a god-project — not a data layer but the application layer wearing its name: 93 service
classes + Hangfire jobs + envelope encryption + AI orchestration (Anthropic SDK) + email (MailKit) + PowerShell
AST analysis (System.Management.Automation). Every consumer of "persistence" transitively drags MailKit + the
PowerShell SDK + the Anthropic client, and the blast radius of any package bump is the whole server. Mechanical
move now; a compatibility event for every plugin/test after GA.

Scope:
1. Create KrakenDeploy.Server.Application (or .Services). Move the 93 services, Jobs, Encryption, AI
   orchestration, email, and the PowerShell-AST gate there. Leave Server.Data as ONLY: KrakenDbContext,
   configurations, migrations, interceptors, and the storage primitives.
2. Fix the resulting reference graph: Server.Application → Server.Data + Server.Core; Server.Transport and
   Server → Application. This also lets you cut the Mcp → Server.Transport edge (Mcp's one AdhocTools
   dependency): introduce an IAdhocDispatcher in Core/Application and have Mcp depend on that, not Transport.
3. Keep it a pure namespace/project move — no behaviour change. Update using directives and DI registration
   extension locations.

Acceptance: builds clean; Server.Data no longer references MailKit / System.Management.Automation / Anthropic;
Mcp no longer references Server.Transport; all tests green; DI boots. CONTRACT CHANGE: none (internal
structure). Coordinate with D7 (arch tests will encode the new boundaries).
Branch: refactor/split-server-data-application
```

### D5 — Decouple ControlPlane/blue-green/HA from the MultiAccount flag; quarantine SaaS (T2-7 + strategic defer)

```text
TASK: Formalise the audit's strategic recommendation: DEFER multi-account SaaS for v1, ship the on-prem core
clean, but KEEP and decouple the blue-green/HA machinery (which on-prem HA needs). Today the on-prem product
compiles and ships the entire SaaS layer via a hard Server → ControlPlane → Server.Data reference; blue-green's
automation (DrainModeHangfireStopper, ReleaseDrainWatcher) registers ONLY when MultiAccount:Enabled; and the
release registry lives in the Catalog DB which only exists in SaaS mode.

Scope:
1. Sever the compile-time coupling: hide ControlPlane + the account-plumbing behind a thin IPlatformControlPlane
   (or an opt-in project reference / separate solution) with a null on-prem implementation, so the shipped
   on-prem binary does not compile the SaaS surface (DB-per-account provisioning, FileSecretStore, fleet
   migrator). Keep the MultiAccount:Enabled fail-fast (Program.cs) — do NOT unblock the boot for v1.
2. Decouple blue-green/HA from the account flag: register DrainModeHangfireStopper and ReleaseDrainWatcher in
   the single-instance recurring-job path too; give the release registry (app_releases/platform_settings) a
   home in single-instance mode (a small always-present platform DB, or the app DB) so on-prem HA gets
   zero-downtime upgrades without standing up the SaaS catalog. The Router is already standalone.
3. Reserve the per-account DEK SCHEMA now (T2-8): data_encryption_keys.account_id already exists; have the
   account provisioner reserve a DEK row shape so a future SaaS revival doesn't need a rekey migration. No
   feature work — just don't paint the schema into a corner.
4. Document the SaaS quarantine: revival is gated on the boundary tests the audit flagged as missing
   (AccountResolutionMiddleware, CatalogAccountResolver, HostParser, AccountProvisioner have ZERO tests).

Acceptance: `dotnet build` of the on-prem product does not compile ControlPlane/provisioning/FileSecretStore;
on-prem HA can do a blue-green server upgrade WITHOUT MultiAccount:Enabled; the SaaS fail-fast still holds; a
doc records the revival gate. CONTRACT CHANGE: internal structure + the release-registry location — note it.
Branch: refactor/decouple-controlplane-onprem
```

### D6 — DbContext factory mode-dependent pooling (T2-9)

```text
TASK: AddDbContextFactory<KrakenDbContext> is registered Scoped UNCONDITIONALLY, purely so OnConfiguring can
read the per-request IAccountContext in multi-account mode. In single-instance the connection is fixed, so
on-prem gets NO context pooling and every singleton that needs the DB does the IServiceScopeFactory dance.
Make the factory shape mode-dependent.

Scope:
1. On-prem (MultiAccount disabled): register a pooled Singleton DbContextFactory (AddPooledDbContextFactory)
   with a fixed connection — the correct, faster shape. In multi-account: keep the Scoped account-routing
   factory (OnConfiguring reads IAccountContext). Select at startup based on the flag.
2. Verify the singletons that adopted the IServiceScopeFactory workaround (DekProvider, LicenseUsageCounter,
   the SettingsService replacements, etc.) still work under both shapes; simplify where the pooled path makes
   the workaround unnecessary on-prem (optional).
3. Confirm no captive dependency is introduced (Dev host ValidateScopes must pass) — coordinate with C3.

Acceptance: on-prem boots with a pooled Singleton factory and passes scope validation; multi-account still
routes per-account correctly; a basic throughput check shows pooling is active on-prem. CONTRACT CHANGE: none.
Sequence AFTER D5 (the flag boundary must be clean first).
Branch: refactor/dbcontext-factory-pooling
```

### D7 — Architecture-enforcement tests (T2-10)

```text
TASK: The two load-bearing layering invariants (Agent must not reference Server.*; Execution must reference
nothing internal) are enforced only by csproj discipline and review. One careless edit re-couples them and the
compiler happily obliges. Add cheap tests that make today's verified-true invariants forever-true.

Scope:
1. Add a small test project using NetArchTest (or equivalent) asserting: Agent + Agent.Transport have zero
   dependency on any Server.* assembly; Execution depends on nothing internal (Octostache-only); Mcp does not
   reference Server.Transport (after D4); Cli depends only on Contracts; after D4, Server.Data does not
   reference MailKit/System.Management.Automation/Anthropic and the settings DbSet is only touched by
   SettingsService.
2. Add a Router↔ControlPlane schema contract test: a Router.Tests fixture that runs the ControlPlane
   migrations and asserts the two raw SQL queries in the release-snapshot cache (app_releases,
   platform_settings) still parse/execute — so a ControlPlane column rename fails the build, not production.

Acceptance: the arch tests pass on the current tree and FAIL if a forbidden reference is added (prove it by
temporarily adding one locally); the Router schema test fails if a referenced column is renamed. CONTRACT
CHANGE: none. Sequence AFTER D4 and D5 (their boundaries are what the tests encode).
Branch: test/architecture-boundaries
```

---

## 7. Suggested execution order (condensed)

1. **Now (hours–1 day each, ship immediately):** A1, A3, A5, C1, C5.
2. **Security week:** A2, A4, A6, A7, A8.
3. **Engine fortnight (the bet):** B1, B2, B3 → B4, B5 → B6 → B7 → B8. (B6 is the pre-freeze wire pass.)
4. **Ops (parallel with engine):** C2, C3, C4, C6.
5. **Pre-freeze structural:** D1 → D2, then D3, D4 → D5 → D6, D7.
6. **Then:** finish-plan WP14 (docs, expanded per §1a), cut the v1 line (schema baseline, forward-only expand/contract migrations, contract freeze, agent-JWT validation on, backup→restore proven by C1's CI).

Everything here is inside the "breaking changes allowed" window. The window closes at the v1 line — which is why the Phase D contract/schema changes and B6's wire pass must land before it.
