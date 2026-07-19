using System.Text.Json;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Octostache;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Background service that reads runbook-run IDs from the <see cref="RunbookRunChannel"/>,
/// resolves variables, builds a <see cref="DeploymentPlan"/> (using the RunbookRun ID
/// as the plan's DeploymentId), and sends it to the target agent.
/// The agent is unaware of the distinction — it executes the plan and calls back the
/// same <see cref="AgentHub"/> methods. The hub tries both tables on every callback.
/// </summary>
public sealed class RunbookRunWorker(
    RunbookRunChannel queue,
    IAgentConnectionRegistry registry,
    IHubContext<AgentHub, IAgentHubClient> agentHub,
    IServiceScopeFactory scopeFactory,
    InFlightWorkGauge inFlightGauge,
    TimeProvider timeProvider,
    ILogger<RunbookRunWorker> logger)
    : BackgroundService
{
    // The dispatching account for the in-flight run, set per fire-and-forget dispatch
    // so concurrent dispatches don't clobber each other; read by the Phase 5 guard.
    private readonly AsyncLocal<Guid> _dispatchAccountId = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
        {
            // Tracked by the in-flight gauge so a Draining blue-green slot can
            // report when this instance's orchestration work hits zero (§5/§9).
            _ = TrackedDispatchAsync(item, stoppingToken);
        }
    }

    private async Task TrackedDispatchAsync(TenantWorkItem item, CancellationToken ct)
    {
        using var tracking = inFlightGauge.Track();
        await DispatchAsync(item, ct).ConfigureAwait(false);
    }

    // Resolve the run's account (multi-account) and run the dispatch under it; the
    // account flows via AsyncLocal into DispatchCoreAsync's scope. Guid.Empty
    // (single-instance) uses the fixed connection.
    private async Task DispatchAsync(TenantWorkItem item, CancellationToken ct)
    {
        _dispatchAccountId.Value = item.AccountId;
        if (item.AccountId == Guid.Empty)
        {
            await DispatchCoreAsync(item.Id, ct).ConfigureAwait(false);
            return;
        }

        await using var accountScope = scopeFactory.CreateAsyncScope();
        var account = await accountScope.ServiceProvider
            .GetRequiredService<IAccountResolver>()
            .ResolveByIdAsync(item.AccountId, ct)
            .ConfigureAwait(false);
        if (account is null)
        {
            logger.LogError(
                "RunbookRunWorker: account {AccountId} not found for run {RunId}.",
                item.AccountId, item.Id);
            return;
        }

        using (accountScope.ServiceProvider.GetRequiredService<IAccountContext>().WithAccount(account))
        {
            await DispatchCoreAsync(item.Id, ct).ConfigureAwait(false);
        }
    }

    private async Task DispatchCoreAsync(Guid runId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
        var spaceContext = scope.ServiceProvider.GetRequiredService<ISpaceContext>();
        var variableService = scope.ServiceProvider.GetRequiredService<VariableService>();
        var serverBaseUrl = scope.ServiceProvider
            .GetRequiredService<IConfiguration>()["Server:BaseUrl"];

        try
        {
            // Worker scope has no active Space (no HttpContext → DefaultSpaceId);
            // a run triggered in a non-Default Space (RunbookRun.SpaceId = the
            // parent runbook's Space) would be hidden by the global filter and
            // sit Queued forever. Resolve its Space filter-free, then scope the
            // whole unit of work — including VariableService.ResolveWithStepsAsync,
            // which relies on the ambient filter to scope variable sets.
            var runSpaceId = await db.RunbookRuns.IgnoreQueryFilters()
                .Where(r => r.Id == runId)
                .Select(r => (Guid?)r.SpaceId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (runSpaceId is null)
            {
                logger.LogWarning("RunbookRunWorker: run {Id} not found.", runId);
                return;
            }
            using var spaceScope = spaceContext.WithSpace(runSpaceId.Value);

            var run = await db.RunbookRuns
                .Include(r => r.Runbook)
                    .ThenInclude(rb => rb.Project)
                .Include(r => r.Environment)
                .Include(r => r.Targets).ThenInclude(a => a.Target!)
                .Include(r => r.Tenant)
                .FirstOrDefaultAsync(r => r.Id == runId, ct)
                .ConfigureAwait(false);

            if (run is null)
            {
                logger.LogWarning("RunbookRunWorker: run {Id} not found.", runId);
                return;
            }

            // Single-target dispatch: the assignment set is the authority now (the
            // old single TargetId column is gone). First-assigned target is
            // canonical. Full multi-target/waves parity comes with the follow-up
            // merge of runbook runs into the deployment orchestrator.
            var target = run.ResolvedTargets().FirstOrDefault();
            if (target is null)
            {
                await FailAsync(db, run, "No target assigned to runbook run.", ct).ConfigureAwait(false);
                return;
            }

            var connectionId = registry.GetConnectionId(target.Id);
            if (connectionId is null)
            {
                await FailAsync(db, run, "Target is offline.", ct).ConfigureAwait(false);
                return;
            }

            // P3-8 Phase 5 — cross-account dispatch guard (defense-in-depth). A live
            // connection whose recorded account differs from this run's dispatch
            // account must never receive the plan (structurally impossible given
            // globally-unique target ids validated at connect; fail closed regardless).
            if (_dispatchAccountId.Value != Guid.Empty
                && registry.GetAccountForTarget(target.Id) != _dispatchAccountId.Value)
            {
                logger.LogError(
                    "Cross-account dispatch blocked for runbook run {Run}: target {Target}'s " +
                    "live connection belongs to account {ConnectionAccount}, not the dispatch " +
                    "account {DispatchAccount}.",
                    run.Id, target.Id,
                    registry.GetAccountForTarget(target.Id), _dispatchAccountId.Value);
                await FailAsync(db, run, "Cross-account connection blocked at dispatch.", ct)
                    .ConfigureAwait(false);
                return;
            }

            if (run.ProcessSnapshot.Count == 0)
            {
                await FailAsync(db, run, "Runbook has no steps.", ct).ConfigureAwait(false);
                return;
            }

            // ── Resolve variables ────────────────────────────────────────────
            var targetRoles = target.Roles;
            // Resolve deployment-wide variables + per-step deltas live (runbooks
            // don't use a frozen release snapshot). channelId is null — runbooks
            // aren't channel-scoped.
            var stepResolution = await variableService.ResolveWithStepsAsync(
                run.Runbook.ProjectId,
                run.EnvironmentId,
                target.Id,
                targetRoles,
                run.TenantId,
                channelId: null,
                steps: run.ProcessSnapshot.Select(s => (s.Id, s.Name)).ToList(),
                ct: ct).ConfigureAwait(false);
            var rawVars = stepResolution.DeploymentWide;

            // ── Build Octostache dictionary ───────────────────────────────────
            var varDict = new VariableDictionary();

            // Octopus.Deployment.Tenant.Tags — canonical strings of the tenant's
            // applied tags (extended tag sets), mirroring the deployment path.
            IReadOnlyList<string>? tenantTagCanonicals = null;
            if (run.TenantId is { } tenantIdForTags)
            {
                tenantTagCanonicals = await TagService
                    .GetTenantTagCanonicalsAsync(db, tenantIdForTags, ct).ConfigureAwait(false);
            }

            var systemVars = OctopusSystemVariablesBuilder.BuildForRunbookRun(
                run,
                run.Runbook,
                run.Runbook.Project,
                run.Environment,
                target,
                run.Tenant,
                run.ProcessSnapshot,
                serverBaseUrl,
                tenantTagCanonicals);

            var flatVars = new Dictionary<string, string>(systemVars, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, val) in systemVars)
            {
                varDict[k] = val;
            }
            var arrayVars = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, value) in rawVars)
            {
                if (value.StartsWith('['))
                {
                    try
                    {
                        var items = JsonSerializer.Deserialize<string[]>(value) ?? [];
                        arrayVars[name] = items;
                        var joined = string.Join(", ", items);
                        flatVars[name] = joined;
                        varDict[name] = joined;
                        for (var i = 0; i < items.Length; i++)
                        {
                            varDict[$"{name}[{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}]"] = items[i];
                        }
                        continue;
                    }
                    catch (JsonException) { }
                }
                flatVars[name] = value;
                varDict[name] = value;
            }

            // ── Build plan via M15.2 flattener ────────────────────────────────
            // The flattener walks the (potentially tree-shaped) snapshot,
            // expanding Step Groups + ForEach iterations, with Octostache
            // substitution applied per emitted plan. Mirrors the
            // DeploymentWorker path. Pre-M15 runbook snapshots are flat
            // (every step's ParentStepId is null → flattener walks them
            // as top-level steps, behaviour identical to pre-flattener).
            var snapshotSteps = run.ProcessSnapshot
                .OrderBy(s => s.SortOrder)
                .ToArray();

            var flatten = DeploymentPlanFlattener.Flatten(
                snapshotSteps, arrayVars, varDict);

            // Process flatten warnings — for runbooks we don't have the
            // full audit + log infrastructure the orchestrator does, but
            // we still log at the appropriate level so operators see what
            // happened in the run's log stream.
            foreach (var w in flatten.Warnings)
            {
                var level = w.Kind == DeploymentPlanFlattener.WarningKind.ForEachEmpty
                    ? Microsoft.Extensions.Logging.LogLevel.Information
                    : Microsoft.Extensions.Logging.LogLevel.Error;
                logger.Log(level,
                    "Runbook {RunId} flatten warning [{Kind}] for step '{Step}': {Detail}",
                    run.Id, w.Kind, w.Source.Name, w.Detail);

                // ForEachUnresolved + Required parent → fail the run.
                // Mirrors the DeploymentWorker Required gate.
                if (w.Kind == DeploymentPlanFlattener.WarningKind.ForEachUnresolved
                    && w.Source.Required)
                {
                    await FailAsync(db, run,
                        $"Required ForEach step '{w.Source.Name}' could not " +
                        $"resolve its collection: {w.Detail}", ct).ConfigureAwait(false);
                    return;
                }
            }

            // Attach per-step variable deltas (step/action scope), keyed by source
            // snapshot Id — the agent overlays them per step, same as the
            // deployment path.
            var steps = flatten.Plans;
            if (stepResolution.PerStepDelta.Count > 0)
            {
                for (var i = 0; i < steps.Length; i++)
                {
                    if (stepResolution.PerStepDelta.TryGetValue(
                            flatten.SnapshotByPlanIndex[i].Id, out var stepDelta))
                    {
                        steps[i] = steps[i] with { StepVariables = stepDelta };
                    }
                }
            }

            // The plan uses RunbookRun.Id as DeploymentId — AgentHub resolves both tables.
            // B2: runbook runs get a DispatchId too (uniform logging / dedup), but no
            // sub-plan slot is registered — their completion takes the hub's direct
            // finalize path by design, where the IsTerminal guard dedups.
            var plan = new DeploymentPlan(
                DeploymentId: run.Id,
                EnvironmentName: run.Environment.Name,
                Steps: steps,
                Variables: flatVars,
                ArrayVariables: arrayVars,
                SensitiveVariableNames: stepResolution.SensitiveNames,
                DispatchId: Guid.NewGuid());

            // ── B1: atomic claim (Queued→Running) ──────────────────────────
            // Exactly one wake-up wins the row; a duplicate enqueue or a row no
            // longer Queued bails here instead of the old blind Running write
            // (which had no status guard at all on this path).
            if (!await ServerTaskLease.TryClaimAsync(db, run.Id, timeProvider, ct)
                    .ConfigureAwait(false))
            {
                logger.LogInformation(
                    "RunbookRunWorker: run {Id} was not claimable (already claimed by another " +
                    "wake-up or no longer Queued); skipping dispatch.",
                    runId);
                return;
            }

            // Mirror the claim onto the tracked entity, NOT-modified (ExecuteUpdate
            // bypassed the change tracker; leaving these dirty would let a later
            // SaveChanges blindly re-assert Running).
            ServerTaskLease.MirrorClaim(db, run, timeProvider);

            // E9 (INTERIM — superseded by the D1 engine merge, which brings runbook
            // runs under B3's disconnect handling). The connection captured before
            // the claim can go stale during variable resolution + flatten. Pushing
            // a plan to a dead connection id is a silent SignalR no-op that zombies
            // the run until the MaxRunbookRunDuration ceiling — so re-verify liveness
            // at hand-off and fast-fail if the target went away (the operator re-runs;
            // a late agent completion would be swallowed by the terminal guard).
            connectionId = registry.GetConnectionId(target.Id);
            if (connectionId is null)
            {
                await FailAsync(db, run, "Target went offline before the plan could be dispatched.", ct)
                    .ConfigureAwait(false);
                return;
            }

            // Re-assert the P3-8 Phase 5 cross-account guard on the RE-FETCHED
            // connection: the pre-claim guard above validated the connection
            // captured before variable resolution, but this is a different
            // connection id (a reconnect during that window), so it must clear the
            // same check before the plan is pushed. Structurally near-impossible
            // (target ids are globally unique to one account) — fail closed anyway.
            if (_dispatchAccountId.Value != Guid.Empty
                && registry.GetAccountForTarget(target.Id) != _dispatchAccountId.Value)
            {
                logger.LogError(
                    "Cross-account dispatch blocked at hand-off for runbook run {Run}: target " +
                    "{Target}'s re-fetched connection belongs to account {ConnectionAccount}, not " +
                    "the dispatch account {DispatchAccount}.",
                    run.Id, target.Id,
                    registry.GetAccountForTarget(target.Id), _dispatchAccountId.Value);
                await FailAsync(db, run, "Cross-account connection blocked at dispatch.", ct)
                    .ConfigureAwait(false);
                return;
            }

            logger.LogInformation(
                "Dispatching runbook run {RunId} ({Runbook}) to connection {Conn}.",
                runId, run.Runbook.Name, connectionId);

            await agentHub.Clients.Client(connectionId)
                .RunDeploymentAsync(plan)
                .ConfigureAwait(false);

            // B1: hand-off — the agent now owns execution and reports terminal
            // status via AgentHub even if this server restarts meanwhile. Release
            // the lease so the orphan reconciler (which only ever targets
            // DEPLOYMENTS) has no claim to misread. (No entity mirror needed —
            // nothing reads the tracked run after this point.)
            await ServerTaskLease.ReleaseAsync(db, run.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unhandled error dispatching runbook run {RunId}.", runId);

            await using var errorScope = scopeFactory.CreateAsyncScope();
            var errorDb = errorScope.ServiceProvider.GetRequiredService<KrakenDbContext>();
            // Fresh scope → DefaultSpaceId; load filter-free so a non-Default-Space
            // run is still found, then scope FailAsync to its Space.
            var run = await errorDb.RunbookRuns.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == runId, ct).ConfigureAwait(false);
            if (run is not null)
            {
                using var _ = errorScope.ServiceProvider
                    .GetRequiredService<ISpaceContext>().WithSpace(run.SpaceId);
                await FailAsync(errorDb, run, ex.Message, ct).ConfigureAwait(false);
            }
        }
    }

    // M15.2 follow-up: SubstituteConfig moved into DeploymentPlanFlattener
    // so per-iteration variable values resolve correctly. The flattener
    // owns the per-ForEach-iteration variable bag.

    private static async Task FailAsync(
        KrakenDbContext db, Core.Domain.Runbooks.RunbookRun run, string reason, CancellationToken ct)
    {
        // B5: this was the last BLIND terminal write on the spine — no status
        // guard at all, so a run cancelled between the B1 claim and a flatten
        // failure (or already reaped by the B3 reconciler) was flipped
        // Cancelled/Failed → Failed with a fresh CompletedUtc. The guarded
        // writer re-reads the authoritative status and lets the recorded
        // verdict stand.
        await ServerTaskStatusWriter.TryTransitionAsync(
            db, run, static r =>
            {
                r.Status = DeploymentStatus.Failed;
                r.CompletedUtc = DateTimeOffset.UtcNow;
                // B1: terminal — release the dispatch lease.
                r.ClaimedBy = null;
                r.LeaseUntil = null;
            }, ct: ct).ConfigureAwait(false);
    }
}
