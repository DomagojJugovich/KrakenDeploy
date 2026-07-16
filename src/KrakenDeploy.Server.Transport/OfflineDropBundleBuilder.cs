using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        var serverBaseUrl = config["Server:BaseUrl"];

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
        var ctx = await DeploymentWorker.BuildTargetDispatchContextAsync(
            logger, deployment, target, snapshotSteps, variableService,
            serverBaseUrl, dbFactory, ct).ConfigureAwait(false);

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
        var onlineOnly = plan.Steps
            .Where(s => s.StepType is "Octopus.DeployRelease" or "Octopus.Manual")
            .Select(s => s.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (onlineOnly.Count > 0)
        {
            throw new InvalidOperationException(
                "Offline drop cannot run server-orchestrated steps: " +
                $"{string.Join(", ", onlineOnly)}. Remove them from the process or " +
                "deploy this project to an online target.");
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
        Guid deploymentId, IServiceProvider sp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sp);

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
