using System.Text.Json;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Settings;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// Builds the offline-drop bundle for a deployment. The bundle is a pure
/// function of the frozen release snapshot + the target's offline-drop config,
/// so it can be (re)produced deterministically from persisted state — at
/// dispatch (via <see cref="DeploymentWorker"/>) and again on demand from the
/// UI/API (regenerate).
///
/// <para>
/// <strong>Statelessness / lifetime</strong>: this is registered as a
/// <em>singleton</em> so the singleton <see cref="DeploymentWorker"/>
/// (a <c>BackgroundService</c>) can consume it without a captive-dependency
/// violation. It holds only an <see cref="ILogger"/>; every scoped collaborator
/// (DbContext factory, variable/package/encryption services, audit log) is
/// resolved from the <see cref="IServiceProvider"/> the <em>caller</em> passes,
/// so the work always runs in the caller's scope (worker dispatch scope,
/// HTTP request scope, or Blazor circuit scope) with the correct
/// account + Space context.
/// </para>
/// </summary>
public sealed class OfflineDropBundleBuilder(ILogger<OfflineDropBundleBuilder> logger)
{
    /// <summary>
    /// Step types a drop bundle must REFUSE to carry, because they are orchestrated by the
    /// server and an air-gapped runner has nothing to orchestrate them with: a
    /// <c>DeployRelease</c> cascade has no server to trigger the child, and a
    /// manual-intervention gate has no way to reach an approver.
    /// <para>
    /// WP3-b — this replaced a <c>StepType is "…" or "…"</c> constant pattern, which C#
    /// compiles to ORDINAL, case-SENSITIVE equality, while every other WP3 comparison
    /// (<c>WavePartitioner.ServerOnlyStepTypes</c>,
    /// <c>ManualInterventionGate.GateStepsIn</c>, the server-step guard, the step package's
    /// <c>CanHandle</c>) is <see cref="StringComparison.OrdinalIgnoreCase"/>. Since
    /// <c>ProcessService</c> stores <c>StepType</c> verbatim with no allow-list or
    /// normalisation, a step added as <c>"octopus.manual"</c> by REST, MCP or an import
    /// still gated ONLINE — nothing looked wrong — yet slipped past this refusal into a
    /// bundle, where the offline handler logs "APPROVAL NOT ENFORCED" and returns success.
    /// The deployment then completed with no <c>Interruption</c> row, no audit event and no
    /// step outcome: a complete change-control bypass whose only trace was one warning line
    /// in the task log.
    /// </para>
    /// <para>
    /// Named and internal-visible so a test can assert the casing directly rather than
    /// having to build a whole plan.
    /// </para>
    /// </summary>
    public static bool IsOnlineOnlyStepType(string? stepType)
        => stepType is not null
           && (stepType.Equals(
                   KrakenDeploy.Contracts.Steps.DeployReleaseConfigKeys.StepType,
                   StringComparison.OrdinalIgnoreCase)
               || stepType.Equals(
                   ManualInterventionConfigKeys.StepType,
                   StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds (or rebuilds) the drop bundle for an already-loaded offline-drop
    /// <paramref name="deployment"/> and returns the relative bundle path.
    /// The <paramref name="deployment"/> must have its <c>Release</c>
    /// (+ <c>Project</c>, snapshots) and <c>Environment</c> navigations
    /// loaded; <paramref name="target"/> is the deployment's single assigned
    /// offline-drop target (+ <c>OfflineDropConfig</c>) — offline drops are
    /// single-target by design, and the caller resolves it from the
    /// assignments join.
    /// <para>
    /// Does NOT mutate the deployment row, transition status, or deliver the
    /// bundle — the caller owns those. Throws
    /// <see cref="InvalidOperationException"/> on any pre-flight gate failure
    /// (no variable snapshot, missing bundle key, server-orchestrated steps,
    /// unresolved required <c>ForEach</c>), with the same operator-facing
    /// messages the online dispatch path uses.
    /// </para>
    /// </summary>
    public async Task<string> GenerateOfflineBundleAsync(
        IServiceProvider sp, Deployment deployment, DeploymentTarget target,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sp);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(target);

        var variableService = sp.GetRequiredService<VariableService>();
        var dropBundleService = sp.GetRequiredService<DropBundleService>();
        var stepPackages = sp.GetRequiredService<StepPackageService>();
        var encryption = sp.GetRequiredService<IEncryptionService>();
        var config = sp.GetRequiredService<IConfiguration>();
        var dbFactory = sp.GetRequiredService<IDbContextFactory<KrakenDbContext>>();
        var dataPath = config["Server:DataPath"] ?? "data";
        var serverBaseUrl = sp.GetRequiredService<IOptions<OperationalSettings>>()
            .Value.ServerBaseUrl;

        // Offline drops use the frozen release snapshot, exactly like online —
        // refuse to ship a bundle from an un-snapshotted (pre-feature) release.
        if (deployment.Release.VariableSnapshotUpdatedUtc is null)
        {
            throw new InvalidOperationException(
                $"Release '{deployment.Release.Version}' has no variable snapshot. " +
                "Open the release and click 'Update Variables', then re-deploy.");
        }

        // Per-target bundle encryption key (provisioned when the target was
        // configured as offline-drop). Without it we can't produce plan.enc.
        var bundleKeyEnc = target.OfflineDropConfig?.BundleKeyEncrypted;
        if (string.IsNullOrEmpty(bundleKeyEnc))
        {
            throw new InvalidOperationException(
                "Offline-drop target has no bundle encryption key. Re-save the " +
                "target's offline-drop settings to provision one (and deliver it to " +
                "the target operator out-of-band), then re-deploy.");
        }
        var bundleKey = Convert.FromBase64String(encryption.Decrypt(bundleKeyEnc));

        // Build the SAME plan the online path dispatches (snapshot-resolved,
        // Octostache-substituted, flattened, per-step deltas) so the offline
        // runner executes it through the identical DeploymentExecutor.
        var snapshotSteps = deployment.Release.ProcessSnapshot
            .OrderBy(s => s.SortOrder)
            .ToArray();
        // Offline drop is deployment-only — wrap the loaded Deployment in the
        // deployment dispatch source so the shared context builder resolves
        // variables from the frozen release snapshot (D1 engine merge).
        var ctx = await DeploymentWorker.BuildTargetDispatchContextAsync(
            logger, deployment, new DeploymentDispatchSource(deployment), target, snapshotSteps,
            variableService, serverBaseUrl, dbFactory, ct).ConfigureAwait(false);

        // Required ForEach that couldn't resolve its collection aborts here,
        // mirroring the online gate.
        foreach (var w in ctx.Flatten.Warnings)
        {
            if (w.Kind == DeploymentPlanFlattener.WarningKind.ForEachUnresolved && w.Source.Required)
            {
                throw new InvalidOperationException(
                    $"Required ForEach step '{w.Source.Name}' could not resolve its " +
                    $"collection: {w.Detail}");
            }
        }

        var plan = ctx.Plan;

        // Server-orchestrated step types can't run on an air-gapped box (no
        // server to drive the cascade / approval). Refuse rather than ship a
        // bundle that fails mid-run.
        //
        // WP3 keeps this refusal for Octopus.Manual deliberately, and it is the
        // STRONGER behaviour: a bundle cannot reach an approver, so the only
        // alternatives are to refuse or to pass the gate with no human decision.
        // Since WP3 exists precisely because silently auto-approving a
        // change-control gate is a compliance defect, refusing is the correct end
        // state for an air-gapped target. (The step package's handler still carries
        // a loud "APPROVAL NOT ENFORCED" warning for any runner that reaches it via
        // a hand-built plan.)
        //
        // WP3-b — the comparison is now CASE-INSENSITIVE, via a named predicate a test
        // can reach. See IsOnlineOnlyStepType for why that was a gate bypass.
        var onlineOnly = plan.Steps
            .Where(s => IsOnlineOnlyStepType(s.StepType))
            .Select(s => s.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (onlineOnly.Count > 0)
        {
            throw new InvalidOperationException(
                "Offline drop cannot run server-orchestrated steps: " +
                $"{string.Join(", ", onlineOnly)}. An air-gapped target has no way to " +
                "reach an approver for a manual-intervention gate, and passing one " +
                "unapproved would defeat its purpose. Remove them from the process or " +
                "deploy this project to an online target.");
        }

        // SC4-b: the online path refuses unserved and server-locus types before
        // dispatch (StepTypeExecutionGuard), but that block sits AFTER the
        // offline-drop branch returns, so this path never saw it. The hardcoded
        // pair above only covers the two types that predate the registry — a
        // package declaring executionLocus=server, or a type nothing serves,
        // would otherwise be written into an air-gapped bundle and die on the
        // runner with the opaque "Unknown step type" the guard exists to
        // prevent. Same choke point as the UI regenerate path.
        await using (var guardDb = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var planTypeIds = plan.Steps
                .Select(s => s.StepType.ToLowerInvariant())
                .Distinct()
                .ToList();
            var registryRows = await guardDb.StepTypes.AsNoTracking()
                .Where(t => planTypeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, StringComparer.OrdinalIgnoreCase, ct)
                .ConfigureAwait(false);

            var unserved = plan.Steps
                .Where(s => !registryRows.ContainsKey(s.StepType.ToLowerInvariant()))
                .Select(s => $"{s.Name} ({s.StepType})")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unserved.Count > 0)
            {
                throw new InvalidOperationException(
                    "Offline drop cannot carry steps whose type no installed step package " +
                    $"serves: {string.Join(", ", unserved)}. Install the package that " +
                    "provides the type (or fix the step's type) and regenerate the bundle.");
            }

            // Pin-aware locus: a step executes with the version it is PINNED to,
            // so its pinned manifest's declared locus decides its side — mirroring
            // the online DeploymentWorker guard. Reading the registry serving row
            // alone would falsely refuse a bundle whose step pins an agent-locus
            // version of a now-server-locus type, and (worse) let a server-locus
            // pinned step through when the registry serves an agent-locus version.
            var pinnedNames = plan.Steps
                .Where(s => s.StepPackageName is not null).Select(s => s.StepPackageName!)
                .Distinct().ToList();
            var pinnedVersions = plan.Steps
                .Where(s => s.StepPackageVersion is not null).Select(s => s.StepPackageVersion!)
                .Distinct().ToList();
            var manifestByPin = pinnedNames.Count == 0
                ? new Dictionary<(string, string), string>()
                : (await guardDb.StepPackages.AsNoTracking()
                        .Where(p => pinnedNames.Contains(p.Name) && pinnedVersions.Contains(p.Version))
                        .Select(p => new { p.Name, p.Version, p.ManifestJson })
                        .ToListAsync(ct).ConfigureAwait(false))
                    .ToDictionary(p => (p.Name, p.Version), p => p.ManifestJson);

            StepTypeExecutionLocus LocusOf(DeploymentStepPlan step)
            {
                var typeId = step.StepType.ToLowerInvariant();
                if (step.StepPackageName is not null && step.StepPackageVersion is not null
                    && manifestByPin.TryGetValue((step.StepPackageName, step.StepPackageVersion), out var json))
                {
                    try
                    {
                        var pinnedManifest = StepPackageManifestJson.Deserialize(json);
                        var decl = pinnedManifest.StepTypes.FirstOrDefault(d =>
                            d.Id.Trim().Equals(typeId, StringComparison.OrdinalIgnoreCase));
                        if (decl is not null)
                        {
                            return string.Equals(decl.ExecutionLocus, StepTypeDeclaration.ServerLocus,
                                                 StringComparison.OrdinalIgnoreCase)
                                ? StepTypeExecutionLocus.ServerRunner
                                : StepTypeExecutionLocus.AgentPackage;
                        }
                    }
                    catch (Exception ex) when (
                        ex is JsonException or InvalidOperationException or NotSupportedException)
                    {
                        // Fall back to the registry serving row below.
                    }
                }
                return registryRows.TryGetValue(typeId, out var row)
                    ? row.ExecutionLocus
                    : StepTypeExecutionLocus.AgentPackage;
            }

            var serverLocus = plan.Steps
                .Where(s => LocusOf(s) != StepTypeExecutionLocus.AgentPackage)
                .Select(s => $"{s.Name} ({s.StepType})")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (serverLocus.Count > 0)
            {
                throw new InvalidOperationException(
                    "Offline drop cannot run server-orchestrated steps: " +
                    $"{string.Join(", ", serverLocus)}. Their step package declares " +
                    "server-side execution, which an air-gapped target cannot provide. " +
                    "Remove them from the process or deploy to an online target.");
            }
        }

        // Runner embedding (PerformanceSettings.EmbedOfflineRunner, default true,
        // editable on /configuration/performance): embed the self-contained
        // runner published for the target's RID under
        // {dataPath}/offline-runner/{rid}/ so the bundle needs no .NET on the
        // target (~110 MB/bundle). When off, bundles stay small (data only) and
        // the bootstrap falls back to a KrakenDeploy.Agent on PATH. An absent
        // staged runner degrades gracefully regardless.
        var perfSettings = await sp.GetRequiredService<PerformanceSettingsService>()
            .GetAsync(ct).ConfigureAwait(false);
        string? runnerStageDir = null;
        if (perfSettings.EmbedOfflineRunner)
        {
            var rid = (target.OperatingSystem ?? "")
                .Contains("windows", StringComparison.OrdinalIgnoreCase)
                    ? "win-x64" : "linux-x64";
            runnerStageDir = Path.Combine(dataPath, "offline-runner", rid);
        }

        return await dropBundleService
            .GenerateAsync(deployment, target, plan, bundleKey,
                stepPackages.TryGetArchivePath, dataPath, runnerStageDir, ct: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Regenerates the drop bundle for an offline-drop deployment that is still
    /// awaiting its offline result, records an audit event, and returns the
    /// (unchanged, deterministic) bundle path. Loads the deployment via the
    /// caller-scoped <see cref="IServiceProvider"/> so the active Space filter
    /// and tenant DB (multi-account) resolve correctly.
    /// <para>
    /// Throws <see cref="InvalidOperationException"/> when the deployment does
    /// not exist, is not an offline-drop deployment, or is not in
    /// <see cref="DeploymentStatus.PendingOfflineResult"/>.
    /// </para>
    /// </summary>
    public async Task<string> RegenerateForDeploymentAsync(
        Guid deploymentId, IServiceProvider sp, CallerAuthorization caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sp);
        ArgumentNullException.ThrowIfNull(caller);

        var dbFactory = sp.GetRequiredService<IDbContextFactory<KrakenDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var deployment = await db.Deployments
            .Include(d => d.Release).ThenInclude(r => r.Project)
            .Include(d => d.Environment)
            .Include(d => d.Targets).ThenInclude(a => a.Target!)
            .Include(d => d.Tenant)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Deployment {deploymentId} not found.");

        // T1-8: regeneration re-materialises a secret-bearing deployable, so it is
        // an execute-op gated exactly like DeploymentService.CreateAsync/CancelAsync
        // — DeploymentCreate scoped to THIS deployment's project/environment/tenant.
        // Strict; an Environment=Test grant must not regenerate a Prod bundle.
        // System (worker/parent-step) callers skip it (authorized at origin).
        if (!caller.IsSystem)
        {
            await sp.GetRequiredService<IPermissionEvaluator>().EnsureScopedAsync(
                caller, Permission.DeploymentCreate,
                new PermissionScope(
                    SpaceId: deployment.SpaceId, ProjectId: deployment.ProjectId,
                    EnvironmentId: deployment.EnvironmentId, TenantId: deployment.TenantId),
                ct).ConfigureAwait(false);
        }

        // Offline drops are single-target by design (the dispatch path
        // refuses multi-target sets), so the deployment's one assignment is
        // the bundle's target.
        var targets = deployment.ResolvedTargets();
        if (targets.Count != 1 || targets[0].TransportMode != TransportMode.OfflineDrop)
        {
            throw new InvalidOperationException(
                "Only offline-drop deployments have a drop bundle to regenerate.");
        }
        var target = targets[0];
        if (deployment.Status != DeploymentStatus.PendingOfflineResult)
        {
            throw new InvalidOperationException(
                "The drop bundle can only be regenerated while the deployment is awaiting " +
                $"its offline result (current status: {deployment.Status}).");
        }

        var path = await GenerateOfflineBundleAsync(sp, deployment, target, ct).ConfigureAwait(false);

        // The path is deterministic ({dataPath}/drop-bundles/{id}/drop-{id}.zip)
        // and the file was overwritten in place, so DropBundlePath is unchanged
        // in the common case — persist defensively for older rows that never
        // recorded it. B5: the write is status-guarded — a cancel landing while
        // the bundle regenerated must not be raced by this save (the fresh
        // bundle is orphaned on disk, same as a cancelled dispatch).
        if (deployment.DropBundlePath != path)
        {
            var wrote = await ServerTaskStatusWriter.TryTransitionAsync(
                db, deployment, d => d.DropBundlePath = path,
                canTransitionFrom: static s => s == DeploymentStatus.PendingOfflineResult,
                ct: ct).ConfigureAwait(false);
            if (!wrote)
            {
                throw new InvalidOperationException(
                    "The drop bundle can only be regenerated while the deployment is awaiting " +
                    $"its offline result (current status: {deployment.Status}).");
            }
        }

        // Regeneration re-materialises a secret-bearing deployable (plan.enc is
        // re-encrypted with a fresh nonce) — record who/when for forensics.
        var audit = sp.GetRequiredService<IAuditLog>();
        await audit.RecordAsync(
            AuditEventType.DropBundleRegenerated,
            subjectType: "Deployment",
            subjectId:   deploymentId.ToString(),
            details:     "Offline drop bundle regenerated.",
            ct:          ct).ConfigureAwait(false);

        logger.LogInformation(
            "Drop bundle regenerated for deployment {Id}: {Path}.", deploymentId, path);
        return path;
    }
}
