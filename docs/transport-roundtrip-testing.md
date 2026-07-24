# Transport Round-Trip Testing (B8)

| | |
|---|---|
| **Version** | 1.1 |
| **Date** | 2026-07-24 |
| **Authors** | Domagoj Jugović, Claude (Opus 4.8) |
| **Status** | Approved |
| **Technologies** | .NET 10, SignalR (WebSocket), Kestrel, JWT (HS256), Docker Compose, GitHub Actions |
| **Projects** | KrakenDeploy.Server.Data.Tests, KrakenDeploy.Server, scripts/, .github/workflows/ |

Production-readiness fix **B8** (test gap): no test drove a real
`DeploymentPlan` over real SignalR to a real agent and back — every suite
faked one side of the wire (`FakeAgentHubContext` server-side,
`RecordingAgentHub` agent-side), so a plan-serialization or hub-contract
drift — exactly what B6's wire pass risks — passed 100% of CI.

## The round-trip suite

`TransportRoundTripTests` (Server.Data.Tests, Docker collection) hosts the
**real** stack on both sides:

- **Server**: loopback Kestrel `WebApplication` mapping the real `AgentHub`
  with the production **AgentJwt validation chain** — same issuer/audience,
  the query-string token hand-off SignalR WebSockets require, and the A8
  `atv` revocation check (`AgentTokenValidator`) — plus the real registries
  and a real `DeploymentWorker` built from the app's DI (so the worker and
  the hub share `IPendingSubPlanRegistry` / `IAgentConnectionRegistry`).
- **Agent**: a real `SignalRServerLink` (WebSocket, minted production JWT) +
  `DeploymentExecutor` + `StepPackageLoader`. Handlers resolve **only** via
  step-package pins, so the test assembly itself is staged as the step
  package (the `SamplePluginStepHandler` loader pattern):
  `RoundTripStepHandler` runs inside the loader's plugin ALC with
  produce / consume / block modes. `StepBuilder` gained
  `StepPackageName/Version` threaded into the release snapshot.
- The real **B6 registration leg** runs on every connect and must come back
  `Accepted`.

Three seams pinned:

1. **Full agent round trip** — `Succeeded`; the `AppendLogAsync` leg lands in
   the task log; the `ReportStepCompletedAsync` leg persists the output
   variable; and the consume step *succeeding* is the B4 guard — it returns
   false unless wave 2's sub-plan, built server-side and serialized over the
   wire, carried wave 1's capture.
2. **Server-side step feeds the agent** — a `RunOnServer` step's real-shell
   capture (`ServerScriptStepRunner`) reaches the agent's next wave.
3. **Disconnect mid-step reaches terminal** — a hard connection drop while
   the agent executes (block mode) ends in `Failed` via the B3 disconnect
   monitor (2 s test grace), not a hung dispatch — guarding B3 over the real
   transport.

All three run in the normal Docker test leg (Windows dev box + Linux CI; the
server-side script body is OS-branched).

## Why Docker-category tests skip the Windows CI leg

`ci.yml` runs the build+test matrix on **both** `ubuntu-latest` and
`windows-latest`, but the two legs deliberately run **different** test sets:

- **Linux** runs the full suite, Testcontainers included:
  `dotnet test KrakenDeploy.sln` (with `TESTCONTAINERS_RYUK_DISABLED=true`).
- **Windows** excludes the container tests:
  `dotnet test … --filter "Category!=Docker"`.

Every suite that needs a real Postgres — `TransportRoundTripTests` and the rest
of `KrakenDeploy.Server.Data.Tests` — is tagged `[Trait("Category","Docker")]`
and therefore runs **only on the Linux leg**.

**Why not on Windows CI.** GitHub-hosted `windows-latest` runners cannot run
**Linux** containers. Docker is present, but in Windows-container mode; Linux
containers would need nested virtualization / a WSL2 backend the hosted image
does not provide. `Testcontainers.PostgreSql` pulls the Linux `postgres:16`
image, and the official Postgres image has **no** Windows-container variant
([docker-library/postgres#505](https://github.com/docker-library/postgres/issues/505)
— a request that never shipped). GitHub documents this as a hard rule: Docker
container actions, job containers, and service containers **require a Linux
runner** ([GitHub Docs — About service containers](https://docs.github.com/en/actions/using-containerized-services/about-service-containers)).

**Alternatives considered and rejected:**

- *Unofficial Postgres Windows-container image* (e.g. `stellirin/postgres-windows`)
  — a supply-chain and maintenance liability, and Testcontainers' `PostgreSqlBuilder`
  is built around the Linux image. Rejected.
- *Native Postgres on the Windows runner* (e.g. `ikalnytskyi/action-setup-postgres`)
  plus a fixture that connects to an external instance instead of a Testcontainer
  — viable, but it adds a second Postgres provisioning path to maintain. Deferred:
  the container-backed suites exercise **OS-neutral managed step handlers**
  (`RoundTripStepHandler`, EF/Npgsql), so the Windows-specific coverage gain is
  marginal.

**What still covers Windows.** The behavior that genuinely differs by OS —
`.ps1` execution (UTF-8 + BOM), IIS / Windows-service steps, and the
`IsAgentApphost` path-separator logic — lives in **non-Docker** unit tests that
DO run on the Windows leg. The Windows **dev box** also still runs the full
Docker suite via Docker Desktop; only the hosted Windows **CI** leg skips it.

**Consequence to remember.** A Docker-category test that fails only on Linux is
invisible to the Windows leg — a green Windows job says **nothing** about the
container-backed suites. This bit us on 2026-07-24: `IsAgentApphost` (a
*non-Docker* test) was caught on both legs, but the three `TransportRoundTripTests`
failures surfaced on Linux only, precisely because Windows never runs them.

## The smoke now deploys

`scripts/smoke-test.sh` previously asserted only *connectivity* (agent
Online). Step 6 now triggers a **real deployment** via the dev-only
`POST /api/dev/smoke-deploy` (IsDevelopment-gated, like `smoke-register`):
it seeds environment + lifecycle + project + release whose script step is
pinned to the **boot-seeded bundled step package**, and dispatches through
the production path — worker, hub push, the agent's gRPC step-package
download, a real bash script — then polls the companion dev-only GET until
`Succeeded`. Any other terminal status, or a timeout, fails the smoke and
dumps server + agent logs.

`ci.yml`: the single-instance smoke now runs **on PRs** (a contract drift
must fail the PR, not land on main first); the heavier multi-account and
blue-green smokes keep push-to-main-only gating at step level.

## What making the smoke deploy uncovered

Real deployments need an installed step package (agent handlers resolve only
via pins), and chasing that exposed that **built-in step-package seeding had
never worked anywhere** — three stacked defects, fixed in this WP:

1. **Publish gap** — the seed-archive copy ran `AfterTargets="Build"` only;
   `dotnet publish` never carried it, so every container image shipped with
   no seed directory at all. A publish-time mirror target fixes it.
2. **Signature gate** — the built archives carry the `unsigned-dev-build`
   sentinel and nothing configured `StepPackages:AllowUnsignedUploads`, so
   even a present seed dir installed nothing (the local dev DB had zero
   `step_packages` rows). Development and the smoke opt in;
   **production stays fail-closed deliberately** — packages execute on
   agents, so an implicit trust bypass would be RCE-adjacent. Shipping
   *signed* built-ins is the flagged production follow-up.
3. **CWD-relative default** — the seeder's `seed/step-packages` default
   resolved against the process CWD (`dotnet run` = project dir, not the
   build output). Anchored to `AppContext.BaseDirectory`; a local boot now
   installs the 7 built-ins on first run.

And two **product defects in the agent gRPC path itself** — dead in every
full-pipeline deployment, invisible to every fake-side test (including this
WP's own round-trip host, which doesn't run the full middleware pipeline):

4. **Space-redirect ate gRPC** — `SpaceUrlRedirectMiddleware` 302-redirected
   every `/krakendeploy.v1.<Service>/<Method>` call into `/s/default/…` (a
   dotted *first* segment defeats segment matching; a dot-less *method*
   segment defeats the asset heuristic), and the gRPC client followed the
   redirect as a GET into the UI's 401. Package, step-package and artifact
   transfer have been broken since the Space-in-URL feature landed. Fixed
   with a literal prefix check.
5. **One cleartext port can't serve HTTP/1.1 + HTTP/2** — no TLS ⇒ no ALPN;
   Kestrel answers the h2 preface on a mixed plaintext endpoint with
   `HTTP_1_1_REQUIRED`, so the Caddy topology's assumed
   `h2c://kraken-server:5080` hop never worked. The server now exposes a
   dedicated Http2-only h2c endpoint (5081), the agent gained an optional
   `Server:GrpcUrl` (falls back to `Server:Url`; irrelevant over https), and
   the smoke + on-prem compose + Caddyfile use the split. The Kestrel
   endpoints must be **config** endpoints — `ASPNETCORE_URLS` ones ignore
   protocol configuration. (The on-prem/Caddy edits mirror the
   smoke-verified mechanism but were not run locally.)

Plus two environmental traps: the smoke agent could not even boot against
the in-compose `http://` server since A8's cleartext hardening (CI never
saw it — origin/main predates A8), and the smoke's default compose project
name made its `down -v --remove-orphans` remove a developer's unrelated
local `krakendeploy-*` containers — it now uses a dedicated `-p`. Both
containers also need the images' pre-chowned writable `DataPath` (non-root
users; audit T0-9's unwritable-DataPath observed in the wild).

## Verification notes

- The three round-trip tests run green locally (~7 s total).
- The extended smoke was run **locally end-to-end** (Docker Desktop),
  including the fixed image seeding. The CI-side behavior (PR trigger) is a
  workflow-file change observable only on the next push/PR; flagged at
  check-in.

## Residuals

- The round-trip agent is composed manually (link + executor + loader); the
  agent's hosted-service supervision loop (B2) is exercised by
  `ReconnectE2ETests`, not here.
- Ad-hoc script and artifact-upload (gRPC) legs are not part of the
  round-trip suite.

## References

- `docs/production-fix-prompts-2026-07-13.md` — B8 work package
- `docs/agent-wire-contract.md` (B6), `docs/disconnect-reconciliation.md` (B3)
- [docker-library/postgres#505 — "PostgreSQL as a Windows Container"](https://github.com/docker-library/postgres/issues/505) (feature request, never shipped)
- [GitHub Docs — About service containers](https://docs.github.com/en/actions/using-containerized-services/about-service-containers) (container / service features require a Linux runner)
- [GitHub Docs — GitHub-hosted runners reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)

## History

| Version | Date | Author(s) | Change |
|---|---|---|---|
| 1.0 | 2026-07-16 | Domagoj Jugović, Claude (Opus 4.8) | Initial — B8 round-trip suite, smoke-deploy, uncovered defects |
| 1.1 | 2026-07-24 | Domagoj Jugović, Claude (Opus 4.8) | Added "Why Docker-category tests skip the Windows CI leg" (CI matrix rationale + rejected alternatives) |
