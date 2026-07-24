using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Tenants;
using KrakenDeploy.Server.Core.Domain.Variables;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD and scope-resolution for project <see cref="VariableSet"/>s and <see cref="Variable"/>s.
/// </summary>
public class VariableService(
    IDbContextFactory<KrakenDbContext> dbFactory,
    IEncryptionService encryption,
    IPermissionEvaluator permissions)
{
    // ── T1-8 authoritative scope check ───────────────────────────────────────
    // Project variables are scoped to the owning project (VariableEdit); library
    // sets are Space-level (LibraryVariableSetEdit). Resolve filter-free so a
    // foreign-Space id fails closed.

    private async Task EnsureProjectVariableScopeAsync(
        KrakenDbContext db, CallerAuthorization caller, Guid projectId, CancellationToken ct)
    {
        if (caller.IsSystem)
        {
            return;
        }
        var spaceId = await db.Projects.IgnoreQueryFilters()
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.SpaceId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        await permissions.EnsureScopedAsync(
            caller, Permission.VariableEdit,
            new PermissionScope(SpaceId: spaceId, ProjectId: projectId), ct).ConfigureAwait(false);
    }

    // Resolve the owning project (or library kind) from the set so a by-id edit
    // is authorized against the variable's REAL owner (closes the by-id IDOR).
    private async Task EnsureSetEditScopeAsync(
        KrakenDbContext db, CallerAuthorization caller, Guid setId, CancellationToken ct)
    {
        if (caller.IsSystem)
        {
            return;
        }
        var set = await db.VariableSets.IgnoreQueryFilters()
            .Where(vs => vs.Id == setId)
            .Select(vs => new { vs.Kind, vs.ProjectId, vs.SpaceId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (set is { Kind: VariableSetKind.Library })
        {
            // Library sets are Space-level, not project-scoped.
            await permissions.EnsureScopedAsync(
                caller, Permission.LibraryVariableSetEdit,
                new PermissionScope(SpaceId: set.SpaceId), ct).ConfigureAwait(false);
        }
        else
        {
            // Project set (or unknown → null project → fail closed).
            await permissions.EnsureScopedAsync(
                caller, Permission.VariableEdit,
                new PermissionScope(SpaceId: set?.SpaceId, ProjectId: set?.ProjectId), ct)
                .ConfigureAwait(false);
        }
    }
    // ── Set management ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the <see cref="VariableSet"/> for the project, creating one if it
    /// doesn't exist yet.
    /// </summary>
    public async Task<VariableSet> GetOrCreateSetAsync(
        Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await GetOrCreateSetCoreAsync(db, projectId, ct).ConfigureAwait(false);
    }

    // ── Variable CRUD ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns all variables for a project, with sensitive values redacted.
    /// </summary>
    public async Task<List<VariableDto>> GetVariablesAsync(
        Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var set = await db.VariableSets
            .Include(vs => vs.Variables)
            .FirstOrDefaultAsync(vs => vs.ProjectId == projectId, ct)
            .ConfigureAwait(false);

        if (set is null)
        {
            return [];
        }

        return set.Variables.Select(ToDto).ToList();
    }

    /// <summary>
    /// Returns the full <see cref="Variable"/> entity by id (no redaction).
    /// Used by the edit dialog to populate the scope and metadata fields —
    /// the encrypted Value is intentionally NOT decrypted here, the form
    /// asks the user to re-type sensitive values when editing.
    /// </summary>
    public async Task<Variable?> GetVariableAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Variables
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == id, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new variable in the project's variable set.
    /// Sensitive values are encrypted before storage.
    /// </summary>
    public async Task<Variable> CreateVariableAsync(
        Guid projectId,
        string name,
        string value,
        VariableType type,
        VariableScope? scope,
        CallerAuthorization caller,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(caller);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureProjectVariableScopeAsync(db, caller, projectId, ct).ConfigureAwait(false);
        var set = await GetOrCreateSetCoreAsync(db, projectId, ct).ConfigureAwait(false);

        var storedValue = type switch
        {
            VariableType.Sensitive => encryption.Encrypt(value),
            VariableType.StringArray => NormalizeStringArray(value),
            _ => value,
        };

        var variable = new Variable
        {
            SetId = set.Id,
            Name = name,
            Value = storedValue,
            Type = type,
            Scope = scope ?? new VariableScope(),
        };

        db.Variables.Add(variable);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return variable;
    }

    /// <summary>
    /// Updates an existing variable. Sensitive values are re-encrypted.
    /// <para>
    /// Pass <paramref name="value"/> as <c>null</c> to keep the existing
    /// stored value untouched — useful when editing a Sensitive variable
    /// where the form cannot display the current ciphertext.
    /// </para>
    /// </summary>
    public async Task<Variable?> UpdateVariableAsync(
        Guid id,
        string name,
        string? value,
        VariableType type,
        VariableScope? scope,
        CallerAuthorization caller,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(caller);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var variable = await db.Variables
            .FindAsync([id], ct)
            .ConfigureAwait(false);

        if (variable is null)
        {
            return null;
        }

        await EnsureSetEditScopeAsync(db, caller, variable.SetId, ct).ConfigureAwait(false);

        if (scope is not null && (scope.ProcessStepId.HasValue || scope.ChannelId.HasValue))
        {
            var kind = await db.VariableSets
                .Where(vs => vs.Id == variable.SetId)
                .Select(vs => vs.Kind)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            GuardLibrarySetScope(kind, scope);
        }

        variable.Name = name;
        variable.Type = type;
        variable.Scope = scope ?? new VariableScope();

        if (value is not null)
        {
            variable.Value = type switch
            {
                VariableType.Sensitive => encryption.Encrypt(value),
                VariableType.StringArray => NormalizeStringArray(value),
                _ => value,
            };
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return variable;
    }

    /// <summary>
    /// Replaces a variable's scope with one row per entry in <paramref name="scopes"/>,
    /// atomically — the multi-scope expansion behind the scope popup. With a single
    /// scope the variable is updated in place (id preserved); with several, the
    /// clones are inserted and the original deleted in ONE SaveChanges, so a
    /// failure leaves the original untouched (no lost variable, no stray clones).
    /// Name, value, type and prompt settings are carried over unchanged.
    /// </summary>
    public async Task<List<Variable>> ReplaceVariableScopesAsync(
        Guid id,
        IReadOnlyList<VariableScope> scopes,
        CallerAuthorization caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(caller);
        if (scopes.Count == 0)
        {
            throw new ArgumentException("At least one scope is required.", nameof(scopes));
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var variable = await db.Variables
            .FindAsync([id], ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Variable {id} not found.");

        await EnsureSetEditScopeAsync(db, caller, variable.SetId, ct).ConfigureAwait(false);

        var kind = await db.VariableSets
            .Where(vs => vs.Id == variable.SetId)
            .Select(vs => vs.Kind)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        foreach (var scope in scopes)
        {
            GuardLibrarySetScope(kind, scope);
        }

        if (scopes.Count == 1)
        {
            variable.Scope = scopes[0];
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return [variable];
        }

        if (variable.Type == VariableType.Sensitive)
        {
            // Mirrors the UI rule: sensitive values are never fan-copied.
            throw new InvalidOperationException(
                "Sensitive variables cannot be expanded to multiple scopes.");
        }

        var clones = scopes.Select(s => new Variable
        {
            SetId = variable.SetId,
            SpaceId = variable.SpaceId,
            Name = variable.Name,
            Value = variable.Value,
            Type = variable.Type,
            PromptText = variable.PromptText,
            PromptRequired = variable.PromptRequired,
            Scope = s,
        }).ToList();

        db.Variables.AddRange(clones);
        db.Variables.Remove(variable);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return clones;
    }

    /// <summary>
    /// Deletes a variable by ID. Returns <c>false</c> if not found.
    /// </summary>
    public async Task<bool> DeleteVariableAsync(
        Guid id, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var variable = await db.Variables
            .FindAsync([id], ct)
            .ConfigureAwait(false);

        if (variable is null)
        {
            return false;
        }

        await EnsureSetEditScopeAsync(db, caller, variable.SetId, ct).ConfigureAwait(false);

        db.Variables.Remove(variable);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Cross-project search ───────────────────────────────────────────────

    /// <summary>
    /// Searches variables across projects with optional filters. Returns full
    /// entities (no redaction) for the edit grid. Project and name filters are
    /// applied server-side; scope filters (env, tenant, role, step, target)
    /// are applied in-memory by the caller since Scope is a jsonb column.
    /// </summary>
    public async Task<List<Variable>> SearchVariablesAsync(
        Guid? projectId = null,
        string? nameContains = null,
        IReadOnlyCollection<Guid>? projectIds = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = db.Variables.AsNoTracking()
            .Include(v => v.Set)
            .AsQueryable();

        if (projectId.HasValue)
        {
            query = query.Where(v => v.Set.ProjectId == projectId.Value);
        }
        else if (projectIds is not null)
        {
            // A non-null collection is a hard containment filter: an empty one
            // (e.g. a project tag applied to no project) matches NOTHING —
            // treating it as "no filter" would silently return every variable.
            if (projectIds.Count == 0)
            {
                return [];
            }
            query = query.Where(v => v.Set.ProjectId != null
                                     && projectIds.Contains(v.Set.ProjectId.Value));
        }

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            // ILIKE with escaped wildcards — the UI promises case-insensitive
            // name search, and a literal "%"/"_" in the term must not widen it.
            var pattern = "%" + EscapeLikePattern(nameContains) + "%";
            query = query.Where(v => EF.Functions.ILike(v.Name, pattern, @"\"));
        }

        return await query
            .OrderBy(v => v.Name)
            .ThenBy(v => v.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private static string EscapeLikePattern(string term) =>
        term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    // ── Library variable sets ──────────────────────────────────────────────

    /// <summary>All library variable sets in the active Space, name-ordered, with variables loaded.</summary>
    public async Task<List<VariableSet>> GetLibrarySetsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.VariableSets
            .Where(vs => vs.Kind == VariableSetKind.Library)
            .Include(vs => vs.Variables)
            .OrderBy(vs => vs.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>A single variable set (any kind) by id, with variables loaded.</summary>
    public async Task<VariableSet?> GetSetAsync(Guid setId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.VariableSets
            .Include(vs => vs.Variables)
            .FirstOrDefaultAsync(vs => vs.Id == setId, ct)
            .ConfigureAwait(false);
    }

    public async Task<VariableSet> CreateLibrarySetAsync(
        string name, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var set = new VariableSet
        {
            Kind = VariableSetKind.Library,
            Name = name.Trim(),
            Description = description?.Trim(),
            // SpaceId is stamped by the SpaceScopingInterceptor; ProjectId stays null.
        };
        db.VariableSets.Add(set);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return set;
    }

    public async Task<VariableSet?> UpdateLibrarySetAsync(
        Guid setId, string name, string? description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var set = await db.VariableSets
            .FirstOrDefaultAsync(vs => vs.Id == setId && vs.Kind == VariableSetKind.Library, ct)
            .ConfigureAwait(false);

        if (set is null)
        {
            return null;
        }

        set.Name = name.Trim();
        set.Description = description?.Trim();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return set;
    }

    /// <summary>
    /// Deletes a library set. Its variables and any project inclusions cascade.
    /// Returns false when the set doesn't exist or isn't a library set.
    /// </summary>
    public async Task<bool> DeleteLibrarySetAsync(Guid setId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var set = await db.VariableSets
            .FirstOrDefaultAsync(vs => vs.Id == setId && vs.Kind == VariableSetKind.Library, ct)
            .ConfigureAwait(false);

        if (set is null)
        {
            return false;
        }

        db.VariableSets.Remove(set);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Variables in a specific set (sensitive values redacted) — backs the library-set detail page.</summary>
    public async Task<List<VariableDto>> GetVariablesInSetAsync(Guid setId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var set = await db.VariableSets
            .Include(vs => vs.Variables)
            .FirstOrDefaultAsync(vs => vs.Id == setId, ct)
            .ConfigureAwait(false);

        if (set is null)
        {
            return [];
        }

        return set.Variables.Select(ToDto).ToList();
    }

    // Projects a Variable entity to its DTO, redacting sensitive values. Shared
    // by GetVariablesAsync and GetVariablesInSetAsync so the redaction rule lives
    // in one place.
    private static VariableDto ToDto(Variable v) => new(
        v.Id,
        v.Name,
        v.Type == VariableType.Sensitive ? "***" : v.Value,
        v.Type.ToString(),
        v.Scope);

    /// <summary>Returns the names of all sensitive variables across the project
    /// and its included library sets (for preview masking).</summary>
    public async Task<HashSet<string>> GetSensitiveNamesAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var projectSet = await db.VariableSets
            .Include(vs => vs.Variables)
            .AsNoTracking()
            .FirstOrDefaultAsync(vs => vs.ProjectId == projectId, ct)
            .ConfigureAwait(false);
        if (projectSet is not null)
        {
            foreach (var v in projectSet.Variables.Where(v => v.Type == VariableType.Sensitive))
            {
                names.Add(v.Name);
            }
        }

        var linkIds = await db.ProjectVariableSetLinks
            .Where(l => l.ProjectId == projectId)
            .Select(l => l.VariableSetId)
            .ToListAsync(ct).ConfigureAwait(false);
        if (linkIds.Count > 0)
        {
            var libVars = await db.Variables
                .AsNoTracking()
                .Where(v => linkIds.Contains(v.SetId) && v.Type == VariableType.Sensitive)
                .Select(v => v.Name)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var n in libVars)
            {
                names.Add(n);
            }
        }

        return names;
    }

    /// <summary>Returns the Tag IDs applied to a tenant (for preview scope matching).</summary>
    public async Task<List<Guid>> GetTenantTagIdsAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await TagService.GetTenantTagIdsAsync(db, tenantId, ct).ConfigureAwait(false);
    }

    /// <summary>Creates a variable directly in a given set (project or library).</summary>
    public async Task<Variable> CreateVariableInSetAsync(
        Guid setId,
        string name,
        string value,
        VariableType type,
        VariableScope? scope,
        CallerAuthorization caller,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(caller);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureSetEditScopeAsync(db, caller, setId, ct).ConfigureAwait(false);
        var setKind = await db.VariableSets
            .Where(vs => vs.Id == setId)
            .Select(vs => (VariableSetKind?)vs.Kind)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Variable set {setId} not found.");

        GuardLibrarySetScope(setKind, scope);

        var storedValue = type switch
        {
            VariableType.Sensitive => encryption.Encrypt(value),
            VariableType.StringArray => NormalizeStringArray(value),
            _ => value,
        };

        var variable = new Variable
        {
            SetId = setId,
            Name = name,
            Value = storedValue,
            Type = type,
            Scope = scope ?? new VariableScope(),
        };

        db.Variables.Add(variable);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return variable;
    }

    // ── Project ↔ library-set inclusion ────────────────────────────────────

    /// <summary>Library sets a project includes, in overlay order (ascending SortOrder).</summary>
    public async Task<List<VariableSet>> GetIncludedSetsAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var links = await db.ProjectVariableSetLinks
            .Where(l => l.ProjectId == projectId)
            .OrderBy(l => l.SortOrder)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (links.Count == 0)
        {
            return [];
        }

        var ids = links.Select(l => l.VariableSetId).ToList();
        var sets = await db.VariableSets
            .Where(vs => ids.Contains(vs.Id))
            .Include(vs => vs.Variables)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byId = sets.ToDictionary(s => s.Id);
        return links
            .Where(l => byId.ContainsKey(l.VariableSetId))
            .Select(l => byId[l.VariableSetId])
            .ToList();
    }

    /// <summary>Library sets NOT yet included by a project (candidates to add).</summary>
    public async Task<List<VariableSet>> GetAvailableLibrarySetsAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var includedIds = await db.ProjectVariableSetLinks
            .Where(l => l.ProjectId == projectId)
            .Select(l => l.VariableSetId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return await db.VariableSets
            .Where(vs => vs.Kind == VariableSetKind.Library && !includedIds.Contains(vs.Id))
            .OrderBy(vs => vs.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Includes a library set in a project (idempotent), appended at the end of the overlay order.</summary>
    public async Task IncludeSetAsync(
        Guid projectId, Guid setId, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureProjectVariableScopeAsync(db, caller, projectId, ct).ConfigureAwait(false);

        // Validate BOTH ends are in the current Space before linking (belt; the
        // composite FKs (space_id, project_id)/(space_id, variable_set_id) are the
        // braces). The DbSets are Space-filtered, so a cross-Space id resolves to
        // null/false here — fail fast with a clear error instead of a raw FK
        // violation. space_id is stamped from the (validated in-scope) project.
        var spaceId = await db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.SpaceId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project {projectId} not found in the current Space.");

        if (!await db.VariableSets.AnyAsync(vs => vs.Id == setId, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Variable set {setId} not found in the current Space.");
        }

        var already = await db.ProjectVariableSetLinks
            .AnyAsync(l => l.ProjectId == projectId && l.VariableSetId == setId, ct)
            .ConfigureAwait(false);
        if (already)
        {
            return;
        }

        var maxSort = await db.ProjectVariableSetLinks
            .Where(l => l.ProjectId == projectId)
            .Select(l => (int?)l.SortOrder)
            .MaxAsync(ct)
            .ConfigureAwait(false) ?? -1;

        db.ProjectVariableSetLinks.Add(new ProjectVariableSetLink
        {
            SpaceId = spaceId,
            ProjectId = projectId,
            VariableSetId = setId,
            SortOrder = maxSort + 1,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes a library set from a project's inclusions (idempotent).</summary>
    public async Task ExcludeSetAsync(
        Guid projectId, Guid setId, CallerAuthorization caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureProjectVariableScopeAsync(db, caller, projectId, ct).ConfigureAwait(false);
        var link = await db.ProjectVariableSetLinks
            .FirstOrDefaultAsync(l => l.ProjectId == projectId && l.VariableSetId == setId, ct)
            .ConfigureAwait(false);
        if (link is null)
        {
            return;
        }

        db.ProjectVariableSetLinks.Remove(link);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── Scope resolution ───────────────────────────────────────────────────

    /// <summary>
    /// Resolves the effective variables for a deployment context, combining the
    /// tenant common set, the project's included library variable sets, and the
    /// project's own variables.
    /// <para>
    /// Octopus-compatible precedence: for each name the most-specific matching
    /// scope wins (see <see cref="VariableScope.SpecificityScore"/>). When two
    /// definitions are scoped <i>equally</i>, the higher origin rank breaks the
    /// tie — project over library (higher inclusion order first) over tenant.
    /// </para>
    /// <para>
    /// Sensitive values are decrypted; <see cref="VariableType.StringArray"/> values
    /// are returned as their raw JSON strings (caller separates them for the agent plan).
    /// </para>
    /// </summary>
    public async Task<Dictionary<string, string>> ResolveAsync(
        Guid projectId,
        Guid environmentId,
        Guid? targetId,
        IReadOnlyList<string> targetRoles,
        Guid? tenantId = null,
        Guid? channelId = null,
        Guid? stepId = null,
        IReadOnlyList<Guid>? tenantTagIds = null,
        CancellationToken ct = default)
    {
        var candidates = await BuildLiveCandidatesAsync(projectId, tenantId, ct).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ResolveCandidates(candidates, result, environmentId, targetId, targetRoles, tenantId, channelId, stepId, tenantTagIds: tenantTagIds);
        return result;
    }

    /// <summary>
    /// Live counterpart of <see cref="ResolveFromSnapshotWithStepsAsync"/> — used by
    /// runbook runs and offline-drop bundles, which resolve project variables live
    /// (not from a frozen release snapshot). Returns the deployment-wide manifest
    /// plus per-step deltas; the per-step phase is skipped when no variable is
    /// step-scoped.
    /// </summary>
    public async Task<StepScopedResolution> ResolveWithStepsAsync(
        Guid projectId,
        Guid environmentId,
        Guid? targetId,
        IReadOnlyList<string> targetRoles,
        Guid? tenantId,
        Guid? channelId,
        IReadOnlyList<(Guid StepId, string StepName)> steps,
        IReadOnlyList<Guid>? tenantTagIds = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var candidates = await BuildLiveCandidatesAsync(projectId, tenantId, ct).ConfigureAwait(false);
        return ResolveWithStepsCore(candidates, environmentId, targetId, targetRoles, tenantId, channelId, steps, tenantTagIds);
    }

    /// <summary>
    /// Builds the live candidate set for a project deployment context, in
    /// increasing origin rank: tenant common set, included library sets (by
    /// inclusion SortOrder), then the project's own variables.
    /// </summary>
    private async Task<List<ScopedCandidate>> BuildLiveCandidatesAsync(
        Guid projectId, Guid? tenantId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var candidates = new List<ScopedCandidate>();

        // Tenant common variables — lowest origin rank.
        if (tenantId.HasValue)
        {
            var tenant = await db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tenantId.Value, ct)
                .ConfigureAwait(false);

            if (tenant?.VariableSetId.HasValue == true)
            {
                var tenantSet = await db.VariableSets
                    .Include(vs => vs.Variables)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(vs => vs.Id == tenant.VariableSetId!.Value, ct)
                    .ConfigureAwait(false);

                if (tenantSet is not null)
                {
                    candidates.AddRange(tenantSet.Variables.Select(v =>
                        Candidate(v, TenantOriginRank, "Tenant common")));
                }
            }
        }

        // Included library sets — origin rank = inclusion SortOrder (above tenant,
        // below project; a higher SortOrder breaks ties over a lower one).
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
                if (libSets.TryGetValue(link.VariableSetId, out var set))
                {
                    candidates.AddRange(set.Variables.Select(v =>
                        Candidate(v, link.SortOrder, set.Name ?? "Library")));
                }
            }
        }

        // Project's own variables — highest origin rank.
        var projectSet = await db.VariableSets
            .Include(vs => vs.Variables)
            .AsNoTracking()
            .FirstOrDefaultAsync(vs => vs.ProjectId == projectId, ct)
            .ConfigureAwait(false);

        if (projectSet is not null)
        {
            candidates.AddRange(projectSet.Variables.Select(v =>
                Candidate(v, VariableSnapshot.ProjectLayer, "Project")));
        }

        return candidates;
    }

    /// <summary>
    /// Preview-grade resolution with provenance: for each name, the winning
    /// candidate plus WHERE it came from (source set), the winning scope, its
    /// specificity rank and how many definitions competed. Sensitive winners
    /// are flagged and their values are never decrypted (the preview masks
    /// them anyway, so the plaintext never needs to exist here).
    /// <para>
    /// Also detects <b>ambiguous</b> resolution: when two-or-more definitions
    /// tie at the winning specificity AND the same origin rank with
    /// <i>differing</i> values, the resolver's <c>FirstOrDefault</c> pick is
    /// arbitrary (order-dependent) — the deployment result is not deterministic
    /// and the operator should narrow the scopes. A same-specificity clash
    /// broken by origin (project &gt; library) is deterministic and NOT flagged.
    /// </para>
    /// </summary>
    public async Task<List<VariablePreviewRow>> PreviewResolveAsync(
        Guid projectId,
        Guid environmentId,
        Guid? targetId,
        IReadOnlyList<string> targetRoles,
        Guid? tenantId = null,
        Guid? channelId = null,
        Guid? stepId = null,
        IReadOnlyList<Guid>? tenantTagIds = null,
        CancellationToken ct = default)
    {
        var candidates = await BuildLiveCandidatesAsync(projectId, tenantId, ct).ConfigureAwait(false);
        var rows = new List<VariablePreviewRow>();

        foreach (var group in candidates.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var matching = group
                .Where(c => c.Scope.Matches(environmentId, targetId, targetRoles, tenantId, channelId, stepId, tenantTagIds))
                .ToList();

            var winner = matching
                .OrderByDescending(c => c.Scope.SpecificityScore())
                .ThenByDescending(c => c.OriginRank)
                .FirstOrDefault();

            if (winner is null)
            {
                continue;
            }

            // The set the winner was actually chosen from: same specificity AND
            // same origin. More than one here means the pick was arbitrary.
            var winnerSpec = winner.Scope.SpecificityScore();
            var tied = matching
                .Where(c => c.Scope.SpecificityScore() == winnerSpec && c.OriginRank == winner.OriginRank)
                .ToList();
            // Identical values tying is harmless (same result either way); only a
            // clash of DIFFERENT values is a real non-deterministic ambiguity.
            var ambiguous = tied.Count > 1
                && tied.Select(c => c.Value).Distinct(StringComparer.Ordinal).Count() > 1;

            var sensitive = winner.Type == VariableType.Sensitive;
            rows.Add(new VariablePreviewRow(
                winner.Name,
                sensitive ? "" : winner.Value,
                sensitive,
                winner.Source ?? "Project",
                winner.Scope,
                winnerSpec,
                matching.Count,
                tied.Count,
                ambiguous));
        }

        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Shared per-step resolution over a pre-built candidate set (snapshot or
    /// live). Deployment-wide excludes step-scoped variables; each per-step delta
    /// carries only the names whose winner changes in that step's context.
    /// </summary>
    private StepScopedResolution ResolveWithStepsCore(
        IReadOnlyList<ScopedCandidate> candidates,
        Guid environmentId,
        Guid? targetId,
        IReadOnlyList<string> targetRoles,
        Guid? tenantId,
        Guid? channelId,
        IReadOnlyList<(Guid StepId, string StepName)> steps,
        IReadOnlyList<Guid>? tenantTagIds = null)
    {
        var deploymentWide = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Collect sensitive names across the deployment-wide pass AND every
        // per-step pass — a variable that is only step-scoped and sensitive
        // appears in a PerStepDelta, not DeploymentWide, but its value still
        // reaches the agent and must be redacted.
        var sensitiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ResolveCandidates(candidates, deploymentWide, environmentId, targetId, targetRoles, tenantId, channelId, stepId: null, sensitiveNames, tenantTagIds);

        var perStep = new Dictionary<Guid, Dictionary<string, string>>();

        if (candidates.Any(c => c.Scope.ProcessStepId.HasValue))
        {
            foreach (var (stepId, stepNameValue) in steps)
            {
                var full = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ResolveCandidates(candidates, full, environmentId, targetId, targetRoles, tenantId, channelId, stepId, sensitiveNames, tenantTagIds);

                var delta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, value) in full)
                {
                    if (!deploymentWide.TryGetValue(name, out var dw)
                        || !string.Equals(dw, value, StringComparison.Ordinal))
                    {
                        delta[name] = value;
                    }
                }

                if (delta.Count > 0)
                {
                    perStep[stepId] = delta;
                }
            }
        }

        return new StepScopedResolution(deploymentWide, perStep, sensitiveNames);
    }

    private const int TenantOriginRank = -1;

    private static ScopedCandidate Candidate(Variable v, int originRank, string? source = null) =>
        new(v.Name, v.Value, v.Type, v.Scope, originRank, source);

    /// <summary>
    /// Library variable sets are reusable across projects, so they cannot be
    /// scoped to project-specific dimensions (steps, channels). Defence-in-depth
    /// behind the UI, which already hides those pickers for library sets.
    /// </summary>
    private static void GuardLibrarySetScope(VariableSetKind kind, VariableScope? scope)
    {
        if (kind == VariableSetKind.Library && scope is not null
            && (scope.ProcessStepId.HasValue || scope.ChannelId.HasValue))
        {
            throw new InvalidOperationException(
                "Library variable sets cannot be scoped to steps or channels.");
        }
    }

    /// <summary>
    /// Resolves a flat candidate set into the effective variables for a
    /// deployment context. Octopus-compatible precedence: the most-specific
    /// matching scope wins for each name; an exact specificity tie is broken by
    /// the higher origin rank (project &gt; library &gt; tenant). Sensitive
    /// values are decrypted; StringArray values stay as raw JSON strings.
    /// </summary>
    private void ResolveCandidates(
        IReadOnlyList<ScopedCandidate> candidates,
        Dictionary<string, string> result,
        Guid environmentId,
        Guid? targetId,
        IReadOnlyList<string> targetRoles,
        Guid? tenantId,
        Guid? channelId,
        Guid? stepId,
        ICollection<string>? sensitiveNames = null,
        IReadOnlyList<Guid>? tenantTagIds = null)
    {
        foreach (var group in candidates.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var winner = group
                .Where(c => c.Scope.Matches(environmentId, targetId, targetRoles, tenantId, channelId, stepId, tenantTagIds))
                .OrderByDescending(c => c.Scope.SpecificityScore())
                .ThenByDescending(c => c.OriginRank)
                .FirstOrDefault();

            if (winner is null)
            {
                continue;
            }

            if (winner.Type == VariableType.Sensitive)
            {
                result[winner.Name] = encryption.Decrypt(winner.Value);
                // Carry the sensitivity signal out so the plan can drive log
                // redaction — the flat result dict alone can't distinguish a
                // decrypted secret from a plain string.
                sensitiveNames?.Add(winner.Name);
            }
            else
            {
                result[winner.Name] = winner.Value;  // String: plain; StringArray: JSON string
            }
        }
    }

    // One variable competing to win a name during resolution. OriginRank orders
    // the source (tenant < library-by-SortOrder < project) and breaks ties ONLY
    // when two candidates are scoped with equal specificity. Source is a display
    // label carried for the preview/provenance path; deploy paths leave it null.
    private sealed record ScopedCandidate(
        string Name, string Value, VariableType Type, VariableScope Scope, int OriginRank,
        string? Source = null);

    /// <summary>
    /// Resolves a deployment's effective variable set from a frozen
    /// <see cref="Release.VariableSnapshot"/> instead of live project
    /// variables. Tenant common variables (lowest priority) still resolve
    /// live — the snapshot covers ONLY the project's own variables, mirroring
    /// Octopus's "Update Variables" semantics where tenant variables track
    /// the tenant, not the release.
    /// </summary>
    public async Task<Dictionary<string, string>> ResolveFromSnapshotAsync(
        IReadOnlyList<VariableSnapshot> projectSnapshot,
        Guid environmentId,
        Guid? targetId,
        IReadOnlyList<string> targetRoles,
        Guid? tenantId = null,
        Guid? channelId = null,
        Guid? stepId = null,
        IReadOnlyList<Guid>? tenantTagIds = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projectSnapshot);

        var candidates = await BuildSnapshotCandidatesAsync(projectSnapshot, tenantId, ct).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ResolveCandidates(candidates, result, environmentId, targetId, targetRoles, tenantId, channelId, stepId, tenantTagIds: tenantTagIds);
        return result;
    }

    /// <summary>
    /// Snapshot-path resolution that ALSO produces a per-step delta for every
    /// supplied step (per-step variable scope). The deployment-wide manifest
    /// excludes step-scoped variables; each per-step delta carries only the
    /// names whose winner changes in that step's context. Resolution runs
    /// in-memory over a candidate set built once, so per-step passes are cheap;
    /// the whole per-step phase is skipped when no variable carries a step scope.
    /// </summary>
    public async Task<StepScopedResolution> ResolveFromSnapshotWithStepsAsync(
        IReadOnlyList<VariableSnapshot> projectSnapshot,
        Guid environmentId,
        Guid? targetId,
        IReadOnlyList<string> targetRoles,
        Guid? tenantId,
        Guid? channelId,
        IReadOnlyList<(Guid StepId, string StepName)> steps,
        IReadOnlyList<Guid>? tenantTagIds = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projectSnapshot);
        ArgumentNullException.ThrowIfNull(steps);

        var candidates = await BuildSnapshotCandidatesAsync(projectSnapshot, tenantId, ct).ConfigureAwait(false);
        return ResolveWithStepsCore(candidates, environmentId, targetId, targetRoles, tenantId, channelId, steps, tenantTagIds);
    }

    private async Task<List<ScopedCandidate>> BuildSnapshotCandidatesAsync(
        IReadOnlyList<VariableSnapshot> projectSnapshot,
        Guid? tenantId,
        CancellationToken ct)
    {
        var candidates = new List<ScopedCandidate>();

        // Tenant common variables resolve live (lowest origin rank) — they track
        // the tenant, not the frozen release. Mirrors Octopus "Update Variables".
        if (tenantId.HasValue)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var tenant = await db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tenantId.Value, ct)
                .ConfigureAwait(false);

            if (tenant?.VariableSetId.HasValue == true)
            {
                var tenantSet = await db.VariableSets
                    .Include(vs => vs.Variables)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(vs => vs.Id == tenant.VariableSetId!.Value, ct)
                    .ConfigureAwait(false);

                if (tenantSet is not null)
                {
                    candidates.AddRange(tenantSet.Variables.Select(v => Candidate(v, TenantOriginRank)));
                }
            }
        }

        // Frozen snapshot entries carry their own origin rank in Layer
        // (library = inclusion SortOrder, project = ProjectLayer).
        candidates.AddRange(projectSnapshot.Select(s =>
            new ScopedCandidate(s.Name, s.Value, s.Type, s.Scope, s.Layer)));

        return candidates;
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static async Task<VariableSet> GetOrCreateSetCoreAsync(
        KrakenDbContext db, Guid projectId, CancellationToken ct)
    {
        var set = await db.VariableSets
            .Include(vs => vs.Variables)
            .FirstOrDefaultAsync(vs => vs.ProjectId == projectId, ct)
            .ConfigureAwait(false);

        if (set is not null)
        {
            return set;
        }

        var projectExists = await db.Projects
            .AnyAsync(p => p.Id == projectId, ct)
            .ConfigureAwait(false);

        if (!projectExists)
        {
            throw new InvalidOperationException($"Project {projectId} not found.");
        }

        set = new VariableSet { ProjectId = projectId };
        db.VariableSets.Add(set);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return set;
    }

    /// <summary>
    /// Ensures a StringArray value is stored as a valid JSON array.
    /// Accepts either a JSON array string or a comma-separated string.
    /// </summary>
    private static string NormalizeStringArray(string value)
    {
        value = value.Trim();

        if (value.StartsWith('['))
        {
            // Validate and round-trip to normalise whitespace.
            var parsed = JsonSerializer.Deserialize<List<string>>(value);
            return JsonSerializer.Serialize(parsed ?? []);
        }

        // Treat as comma-separated input from the UI.
        var items = value.Split(',', StringSplitOptions.TrimEntries);
        return JsonSerializer.Serialize(items);
    }
}

// ── DTO ────────────────────────────────────────────────────────────────────────

/// <summary>
/// Variable summary returned by the API. Sensitive values are always redacted to <c>"***"</c>.
/// </summary>
public sealed record VariableDto(
    Guid Id,
    string Name,
    string Value,
    string Type,
    VariableScope Scope);

/// <summary>
/// One row of a variable-resolution preview: the winning value for a name plus
/// its provenance — which set it came from, the winning scope, the scope's
/// specificity rank (higher wins) and how many candidate definitions matched
/// the context. Sensitive winners carry an empty <see cref="Value"/>.
/// <para>
/// <see cref="TiedCount"/> is how many definitions sat at the winning specificity
/// AND origin — the set the winner was chosen from. <see cref="Ambiguous"/> is
/// <c>true</c> when that set holds more than one <i>distinct value</i>, i.e. the
/// resolver's choice was arbitrary and the deployment result is non-deterministic.
/// </para>
/// </summary>
public sealed record VariablePreviewRow(
    string Name,
    string Value,
    bool Sensitive,
    string Source,
    VariableScope Scope,
    int Specificity,
    int CandidateCount,
    int TiedCount,
    bool Ambiguous);

/// <summary>
/// Result of <see cref="VariableService.ResolveFromSnapshotWithStepsAsync"/>:
/// the deployment-wide manifest (step-scoped variables excluded) plus a per-step
/// delta keyed by snapshot step Id — only the names whose winner changes in that
/// step's context. An empty <see cref="PerStepDelta"/> means no variable is
/// step-scoped (the common case), so the agent's per-step overlay is a no-op.
/// </summary>
public sealed record StepScopedResolution(
    Dictionary<string, string> DeploymentWide,
    Dictionary<Guid, Dictionary<string, string>> PerStepDelta,
    IReadOnlyCollection<string> SensitiveNames);
