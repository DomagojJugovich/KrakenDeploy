using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.Channels;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Variables;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Creates and queries releases.
/// A release locks in a specific version of the project's deployment process
/// with pinned package versions.
/// </summary>
/// <remarks>
/// <paramref name="stepPackageResolver"/> is optional so tests/fixtures can
/// keep the legacy single-arg construction. In production it's wired through
/// DI; when null, <see cref="StepSnapshot.StepPackageVersion"/> is copied
/// from the underlying <see cref="ProcessStep"/> as-is (no re-resolution).
/// </remarks>
public class ReleaseService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IPermissionEvaluator permissions,
    StepPackageResolver? stepPackageResolver = null,
    ILogger<ReleaseService>? logger = null)
{
    // ── Create ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a release by snapshotting the current deployment process and
    /// pinning the supplied package version for each step.
    /// </summary>
    /// <param name="projectId">The project to release.</param>
    /// <param name="version">Semantic version string (must be unique per project).</param>
    /// <param name="packageVersions">
    /// Map from step name → package version to pin.
    /// Steps not present in this map use the latest uploaded version of their package.
    /// </param>
    /// <param name="releaseNotes">Optional human-readable notes.</param>
    public async Task<Release> CreateAsync(
        Guid projectId,
        string version,
        CallerAuthorization caller,
        IReadOnlyDictionary<string, string>? packageVersions = null,
        string? releaseNotes = null,
        Guid? channelId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(caller);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // T1-8: authoritative sub-Space authorization — creating a release for
        // THIS project. Strict; runs for every surface (REST/CLI/UI).
        if (!caller.IsSystem)
        {
            var spaceId = await db.Projects.IgnoreQueryFilters()
                .Where(p => p.Id == projectId)
                .Select(p => (Guid?)p.SpaceId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            await permissions.EnsureScopedAsync(
                caller, Permission.ReleaseCreate,
                new PermissionScope(SpaceId: spaceId, ProjectId: projectId), ct).ConfigureAwait(false);
        }

        var duplicate = await db.Releases
            .AnyAsync(r => r.ProjectId == projectId && r.Version == version, ct)
            .ConfigureAwait(false);

        if (duplicate)
        {
            throw new InvalidOperationException(
                $"Release '{version}' already exists for this project.");
        }

        // Resolve the channel (explicit, else the project's default) and its
        // version rule. Octopus semantics: every pinned PRIMARY package version
        // must satisfy the channel's NuGet range + pre-release-tag regex.
        Channel? channel;
        if (channelId.HasValue)
        {
            channel = await db.Channels
                .FirstOrDefaultAsync(c => c.Id == channelId.Value && c.ProjectId == projectId, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Channel {channelId} not found for this project.");
        }
        else
        {
            // No explicit channel (the CLI never sends one; the UI's "use project
            // default"): resolve the default channel so its rules still apply.
            channel = await db.Channels
                .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.IsDefault, ct)
                .ConfigureAwait(false);
        }

        ChannelVersionRule rule;
        try
        {
            rule = channel is null
                ? ChannelVersionRule.None
                : ChannelVersionRule.Parse(channel.VersionRange, channel.VersionTag);
        }
        catch (FormatException ex)
        {
            // A legacy channel rule (authored before channel-save validation, or under
            // the old npm-style grammar) can be unparseable. It was never enforced
            // before this change, so treat it as "no rule" and warn — blocking every
            // release on the channel on upgrade would be a worse regression. New/edited
            // rules are validated at channel save, so this only bites legacy values.
            logger?.LogWarning(ex,
                "Channel '{Channel}' has an unparseable version rule; ignoring it for " +
                "release creation. Re-save the channel with a valid NuGet range / tag " +
                "regex to enforce it.", channel!.Name);
            rule = ChannelVersionRule.None;
        }

        var ruleViolations = new List<string>();

        // Load the current process snapshot (owner = Project).
        var process = await db.Processes
            .Include(p => p.Steps.OrderBy(s => s.SortOrder))
            .FirstOrDefaultAsync(
                p => p.OwnerKind == ProcessOwnerKind.Project && p.OwnerId == projectId, ct)
            .ConfigureAwait(false);

        if (process is null || process.Steps.Count == 0)
        {
            throw new InvalidOperationException(
                "The project has no deployment process steps. Add at least one step before creating a release.");
        }

        // Build the process snapshot, resolving package versions (primary +
        // referenced packages). Pinning the referenced-package versions here
        // matches what we do for the primary PackageVersion — every deploy of
        // this release will then use the exact same set of helper packages.
        var snapshot = new List<StepSnapshot>(process.Steps.Count);
        foreach (var step in process.Steps)
        {
            // Explicit version wins; fall back to the latest uploaded version
            // (only required when the step actually has a primary package).
            string pinned = "";
            if (!string.IsNullOrWhiteSpace(step.PackageId))
            {
                if (packageVersions is not null && packageVersions.TryGetValue(step.Name, out var v))
                {
                    // Explicit version — validate it against the channel rule.
                    pinned = v;
                    var reason = rule.Check(pinned);
                    if (reason is not null)
                    {
                        ruleViolations.Add($"step '{step.Name}' (package '{step.PackageId}'): {reason}");
                    }
                }
                else
                {
                    // Auto-latest — pick the newest uploaded version that satisfies
                    // the channel rule (throws with a clear message if none does).
                    pinned = await ResolveLatestVersionAsync(db, step.PackageId, rule, ct).ConfigureAwait(false);
                }
            }

            // Copy the source Config, then pin any referenced packages in it.
            var snapshotConfig = new Dictionary<string, string>(step.Config);
            await PinReferencedPackagesAsync(snapshotConfig, db, ct).ConfigureAwait(false);

            // D-6: freeze the step-package pin. If the deployment step
            // didn't have one (older row, or no package installed when the
            // step was added), re-resolve "latest installed" *now* so the
            // release is reproducible. Resolver is optional in test fixtures.
            string? snapshotPackageName    = step.StepPackageName;
            string? snapshotPackageVersion = step.StepPackageVersion;
            if (snapshotPackageVersion is null && stepPackageResolver is not null)
            {
                var pin = await stepPackageResolver
                    .ResolveLatestForStepTypeAsync(step.StepType, ct)
                    .ConfigureAwait(false);
                if (pin is not null)
                {
                    snapshotPackageName    = pin.Name;
                    snapshotPackageVersion = pin.Version;
                }
            }

            snapshot.Add(new StepSnapshot
            {
                // M15: freeze the step's Id + parent link so the snapshot
                // tree can be walked at deploy time by DeploymentPlanFlattener.
                Id                          = step.Id,
                ParentStepId                = step.ParentStepId,
                Name                        = step.Name,
                StepType                    = step.StepType,
                PackageId                   = step.PackageId,
                PackageVersion              = pinned,
                TargetRoles                 = [.. step.TargetRoles],
                Config                      = snapshotConfig,
                SortOrder                   = step.SortOrder,
                StepPackageName             = snapshotPackageName,
                StepPackageVersion          = snapshotPackageVersion,
                // M14: freeze step-execution knobs into the snapshot so
                // historical releases keep deploying with the semantics
                // they were cut under.
                Condition                   = step.Condition,
                ConditionVariableExpression = step.ConditionVariableExpression,
                Required                    = step.Required,
                MaxRetries                  = step.MaxRetries,
                RetryDelaySeconds           = step.RetryDelaySeconds,
                TimeoutSeconds              = step.TimeoutSeconds,
                StartTrigger                = step.StartTrigger,
            });
        }

        // Fail the whole release if any explicit package version broke the
        // channel's rules — report every offender at once, not the first.
        if (ruleViolations.Count > 0)
        {
            throw new InvalidOperationException(
                $"Release '{version}' cannot be created on channel '{channel!.Name}' " +
                $"({rule.Describe()}): " + string.Join("; ", ruleViolations) + ".");
        }

        // Variable snapshot — Octopus-style "Update Variables" model.
        // Project variables get frozen at release creation; tenant common
        // variables continue to resolve live (DeploymentWorker overlays them
        // at deploy time). Sensitive values stay encrypted — same ciphertext
        // as the live row so they decrypt at deploy time using whatever key
        // is current then.
        var variableSnapshot = await BuildVariableSnapshotAsync(db, projectId, ct).ConfigureAwait(false);

        var release = new Release
        {
            ProjectId = projectId,
            Version = version,
            ProcessSnapshot = snapshot,
            VariableSnapshot = variableSnapshot,
            VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow,
            ReleaseNotes = releaseNotes,
            ChannelId = channelId,
        };

        db.Releases.Add(release);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return release;
    }

    /// <summary>
    /// Re-snapshots the project's current variable set into the release
    /// (Octopus-style "Update Variables" button). The process snapshot and
    /// step-package pins are NOT touched — only the variable rows are
    /// refreshed plus <see cref="Release.VariableSnapshotUpdatedUtc"/>.
    /// <para>
    /// Returns the updated release. Throws when the release doesn't exist.
    /// </para>
    /// </summary>
    public async Task<Release> UpdateVariablesAsync(
        Guid releaseId, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // T1-8: authoritative sub-Space authorization — re-snapshotting variables
        // is an edit of THIS release's project. Strict; resolve the release's
        // Space/Project filter-free so a foreign-Space release id fails closed.
        // Mirrors CreateAsync; System (internal) callers skip.
        if (!caller.IsSystem)
        {
            var scope = await db.Releases.IgnoreQueryFilters()
                .Where(r => r.Id == releaseId)
                .Select(r => new { r.SpaceId, r.ProjectId })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            await permissions.EnsureScopedAsync(
                caller, Permission.ReleaseEdit,
                new PermissionScope(SpaceId: scope?.SpaceId, ProjectId: scope?.ProjectId), ct)
                .ConfigureAwait(false);
        }

        var release = await db.Releases
            .FirstOrDefaultAsync(r => r.Id == releaseId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Release {releaseId} not found.");

        release.VariableSnapshot = await BuildVariableSnapshotAsync(
            db, release.ProjectId, ct).ConfigureAwait(false);
        release.VariableSnapshotUpdatedUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return release;
    }

    /// <summary>
    /// Reads the project's <see cref="VariableSet"/> and projects each row
    /// into the wire-side <see cref="VariableSnapshot"/> shape — preserves
    /// name + value (ciphertext for sensitive) + type + scope so the
    /// deployment worker can run the same scope-resolution algorithm later.
    /// Returns an empty list when the project has no variable set yet.
    /// </summary>
    private static async Task<List<VariableSnapshot>> BuildVariableSnapshotAsync(
        KrakenDbContext db, Guid projectId, CancellationToken ct)
    {
        var result = new List<VariableSnapshot>();

        // Included library variable sets first, at lower layers (inclusion
        // SortOrder). A later-included set overlays an earlier one; the
        // project's own variables (added below at ProjectLayer) win over all.
        var links = await db.ProjectVariableSetLinks
            .Where(l => l.ProjectId == projectId)
            .OrderBy(l => l.SortOrder)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (links.Count > 0)
        {
            var ids = links.Select(l => l.VariableSetId).ToList();
            var libSets = await db.VariableSets
                .Where(vs => ids.Contains(vs.Id))
                .Include(vs => vs.Variables)
                .AsNoTracking()
                .ToDictionaryAsync(vs => vs.Id, ct)
                .ConfigureAwait(false);

            foreach (var link in links)
            {
                if (libSets.TryGetValue(link.VariableSetId, out var libSet))
                {
                    result.AddRange(libSet.Variables.Select(v => ToSnapshot(v, link.SortOrder)));
                }
            }
        }

        // Project's own variable set last, at the top layer.
        var set = await db.VariableSets
            .Include(vs => vs.Variables)
            .AsNoTracking()
            .FirstOrDefaultAsync(vs => vs.ProjectId == projectId, ct)
            .ConfigureAwait(false);

        if (set is not null)
        {
            result.AddRange(set.Variables.Select(v => ToSnapshot(v, VariableSnapshot.ProjectLayer)));
        }

        return result;
    }

    private static VariableSnapshot ToSnapshot(Variable v, int layer) => new()
    {
        Name  = v.Name,
        Value = v.Value,
        Type  = v.Type,
        Layer = layer,
        Scope = new VariableScope
        {
            EnvironmentId = v.Scope.EnvironmentId,
            TargetId      = v.Scope.TargetId,
            TenantId      = v.Scope.TenantId,
            ChannelId     = v.Scope.ChannelId,
            StepName      = v.Scope.StepName,
            Roles         = v.Scope.Roles is null ? null : [.. v.Scope.Roles],
        },
    };

    // ── Query ──────────────────────────────────────────────────────────────

    public async Task<List<Release>> GetAllAsync(
        Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Releases
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// All releases in the space (newest first), optionally bounded to a
    /// created-date window — powers the global Releases page's range bar.
    /// </summary>
    public async Task<List<Release>> GetAllAsync(
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.Releases
            .Include(r => r.Project)
            .Include(r => r.Channel)
            .AsQueryable();
        if (fromUtc is { } from)
        {
            q = q.Where(r => r.CreatedUtc >= from);
        }
        if (toUtc is { } to)
        {
            q = q.Where(r => r.CreatedUtc < to);
        }
        return await q
            .OrderByDescending(r => r.CreatedUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<Release?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Releases
            .Include(r => r.Project)
            .Include(r => r.Channel)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task<string> ResolveLatestVersionAsync(
        KrakenDbContext db, string packageId, ChannelVersionRule rule, CancellationToken ct)
    {
        var query = db.Packages
            .Where(p => p.PackageId == packageId)
            .OrderByDescending(p => p.UploadedUtc)
            .Select(p => p.Version);

        // No rule (the common case, and always for referenced/helper packages):
        // fetch a single row rather than the whole version history.
        if (!rule.HasRules)
        {
            return await query.FirstOrDefaultAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"No package with ID '{packageId}' has been uploaded. " +
                    "Upload it first or provide an explicit version.");
        }

        var versions = await query.ToListAsync(ct).ConfigureAwait(false);
        if (versions.Count == 0)
        {
            throw new InvalidOperationException(
                $"No package with ID '{packageId}' has been uploaded. " +
                "Upload it first or provide an explicit version.");
        }

        // Channel-aware "latest": newest uploaded version that satisfies the rule.
        return versions.FirstOrDefault(rule.IsSatisfiedBy)
            ?? throw new InvalidOperationException(
                $"No uploaded version of package '{packageId}' satisfies the channel's " +
                $"version rules ({rule.Describe()}). Upload a matching version or pin one explicitly.");
    }

    private static readonly System.Text.Json.JsonSerializerOptions PackageRefJsonOpts =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// Pins any <see cref="PackageReference"/> entries in
    /// <c>config[Octopus.Action.Package.PackageReferences]</c> that don't
    /// have an explicit <c>Version</c>. Mirrors the strict semantics of
    /// <see cref="ResolveLatestVersionAsync"/>: refs to packages with zero
    /// uploaded versions throw rather than silently fall through.
    /// No-op when the key is missing or empty.
    /// </summary>
    private static async Task PinReferencedPackagesAsync(
        Dictionary<string, string> config,
        KrakenDbContext db,
        CancellationToken ct)
    {
        if (!config.TryGetValue(KrakenScriptConfigKeys.PackageReferences, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        List<PackageReference>? parsed;
        try
        {
            parsed = System.Text.Json.JsonSerializer.Deserialize<List<PackageReference>>(
                raw, PackageRefJsonOpts);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException(
                $"Step has malformed {KrakenScriptConfigKeys.PackageReferences} JSON: {ex.Message}", ex);
        }

        if (parsed is null || parsed.Count == 0)
        {
            return;
        }

        var pinned = new List<PackageReference>(parsed.Count);
        foreach (var r in parsed)
        {
            if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.PackageId))
            {
                continue; // skip malformed rows
            }

            if (!string.IsNullOrWhiteSpace(r.Version))
            {
                pinned.Add(r);
                continue;
            }

            // Referenced (helper) packages aren't what the channel's app-version
            // range targets, so they resolve to newest with no channel rule applied.
            var latest = await ResolveLatestVersionAsync(db, r.PackageId, ChannelVersionRule.None, ct).ConfigureAwait(false);
            pinned.Add(r with { Version = latest });
        }

        config[KrakenScriptConfigKeys.PackageReferences] =
            System.Text.Json.JsonSerializer.Serialize(pinned, PackageRefJsonOpts);
    }
}
