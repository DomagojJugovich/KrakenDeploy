using KrakenDeploy.Server.Core.Domain.Tags;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD + application management for the Space-level extended tag sets
/// (docs/extended-tag-sets-plan.md): <see cref="TagSet"/>, <see cref="Tag"/>,
/// and the polymorphic <see cref="TagApplication"/> links.
/// <para>
/// Validation lives here (friendly errors); cardinality is ALSO enforced by
/// the partial unique index on <c>tag_applications</c> as the last line
/// against concurrent writers. Entity-level audit rows come free via
/// <c>AuditLogInterceptor</c> (all three entities are <c>AuditableEntity</c>).
/// </para>
/// </summary>
public class TagService(IDbContextFactory<KrakenDbContext> dbFactory)
{
    // ── Tag sets ─────────────────────────────────────────────────────────────

    /// <summary>All sets in the active Space with their tags, display-ordered.</summary>
    public async Task<List<TagSet>> GetAllSetsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TagSets
            .Include(s => s.Tags.OrderBy(t => t.SortOrder).ThenBy(t => t.Name))
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task<TagSet?> GetSetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TagSets
            .Include(s => s.Tags.OrderBy(t => t.SortOrder).ThenBy(t => t.Name))
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    /// <summary>Sets scoped to one entity kind (feeds the entity tag editors).
    /// Scopes is a jsonb-converted list — not translatable to SQL — so the
    /// (small, Space-scoped) set list is filtered in memory.</summary>
    public async Task<List<TagSet>> GetSetsForKindAsync(
        TaggableEntityKind kind, CancellationToken ct = default)
    {
        var all = await GetAllSetsAsync(ct).ConfigureAwait(false);
        return all.Where(s => s.Scopes.Contains(kind)).ToList();
    }

    public async Task<TagSet> CreateSetAsync(
        string name,
        string? description,
        TagSetType type,
        IReadOnlyCollection<TaggableEntityKind> scopes,
        int sortOrder,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateScopes(scopes);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (await db.TagSets.AnyAsync(s => s.Name == name, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Tag set '{name}' already exists in this space.");
        }

        var set = new TagSet
        {
            Name        = name,
            Description = description,
            Type        = type,
            Scopes      = scopes.Distinct().ToList(),
            SortOrder   = sortOrder,
        };
        db.TagSets.Add(set);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return set;
    }

    /// <summary>
    /// Updates a set's metadata, scopes and type.
    /// <list type="bullet">
    ///   <item><b>Scope removal</b> with existing applications of the removed
    ///     kind(s) throws unless <paramref name="force"/> — then those
    ///     applications are cascaded away in the same save.</item>
    ///   <item><b>Type change</b> is blocked while existing applications would
    ///     violate the new cardinality; Select ↔ FreeText conversion is blocked
    ///     while ANY applications exist. An allowed change restamps the
    ///     denormalized <see cref="TagApplication.SetType"/> rows.</item>
    /// </list>
    /// </summary>
    public async Task<TagSet?> UpdateSetAsync(
        Guid id,
        string name,
        string? description,
        TagSetType type,
        IReadOnlyCollection<TaggableEntityKind> scopes,
        int sortOrder,
        bool force = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateScopes(scopes);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var set = await db.TagSets.FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        if (set is null)
        {
            return null;
        }

        if (await db.TagSets.AnyAsync(s => s.Name == name && s.Id != id, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Tag set '{name}' already exists in this space.");
        }

        var newScopes = scopes.Distinct().ToList();

        // ── Scope removal: confirm-then-cascade ─────────────────────────────
        var removedKinds = set.Scopes.Except(newScopes).ToList();
        if (removedKinds.Count > 0)
        {
            var affected = await db.TagApplications
                .Where(a => a.TagSetId == id && removedKinds.Contains(a.EntityKind))
                .ToListAsync(ct).ConfigureAwait(false);
            if (affected.Count > 0)
            {
                if (!force)
                {
                    throw new InvalidOperationException(
                        $"Removing scope(s) {string.Join(", ", removedKinds)} would delete " +
                        $"{affected.Count} tag application(s). Confirm to proceed.");
                }
                db.TagApplications.RemoveRange(affected);
            }
        }

        // ── Type change: block until compliant ──────────────────────────────
        if (type != set.Type)
        {
            // Validation must ignore applications already staged for deletion by
            // the scope-removal cascade above — they hit the DB (where the rows
            // still exist) but won't survive this save, so counting them would
            // falsely reject a combined remove-scope + retype done in one call.
            var validated = db.TagApplications
                .Where(a => a.TagSetId == id && !removedKinds.Contains(a.EntityKind));

            var isFreeTextConversion = type == TagSetType.FreeText || set.Type == TagSetType.FreeText;
            if (isFreeTextConversion)
            {
                // Tag references and free-text values are not convertible.
                var any = await validated.AnyAsync(ct).ConfigureAwait(false);
                if (any)
                {
                    throw new InvalidOperationException(
                        $"Cannot change '{set.Name}' between a select type and FreeText while " +
                        "tags are applied. Remove all applications of this set first.");
                }
            }
            else if (type == TagSetType.SingleSelect)
            {
                // Multi → Single: every entity may keep at most one tag.
                var violating = await validated
                    .GroupBy(a => new { a.EntityKind, a.EntityId })
                    .Where(g => g.Count() > 1)
                    .CountAsync(ct).ConfigureAwait(false);
                if (violating > 0)
                {
                    throw new InvalidOperationException(
                        $"Cannot make '{set.Name}' single-select: {violating} entit" +
                        $"{(violating == 1 ? "y has" : "ies have")} more than one tag from this set. " +
                        "Reduce those to one tag first.");
                }
            }

            // Allowed — restamp the denormalized SetType on surviving rows in
            // the same save/transaction so the partial unique index stays true.
            // Rows staged for deletion by the scope-removal cascade above are
            // skipped (their tracker entry is already Deleted).
            var remaining = await db.TagApplications
                .Where(a => a.TagSetId == id)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var app in remaining.Where(a => db.Entry(a).State != EntityState.Deleted))
            {
                app.SetType = type;
            }
        }

        set.Name        = name;
        set.Description = description;
        set.Type        = type;
        set.Scopes      = newScopes;
        set.SortOrder   = sortOrder;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return set;
    }

    public async Task<bool> DeleteSetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var set = await db.TagSets.FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        if (set is null)
        {
            return false;
        }

        // Tags + applications cascade at the DB level (real FKs to tag_sets).
        db.TagSets.Remove(set);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Tags ─────────────────────────────────────────────────────────────────

    public async Task<Tag> CreateTagAsync(
        Guid tagSetId,
        string name,
        string? color,
        string? description,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var set = await db.TagSets.FirstOrDefaultAsync(s => s.Id == tagSetId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tag set {tagSetId} not found.");

        if (set.Type == TagSetType.FreeText)
        {
            throw new InvalidOperationException(
                $"'{set.Name}' is a free-text set — it has no predefined tags.");
        }

        if (await db.Tags.AnyAsync(t => t.TagSetId == tagSetId && t.Name == name, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Tag '{name}' already exists in this set.");
        }

        var maxSort = await db.Tags
            .Where(t => t.TagSetId == tagSetId)
            .Select(t => (int?)t.SortOrder)
            .MaxAsync(ct).ConfigureAwait(false) ?? -1;

        var tag = new Tag
        {
            TagSetId    = tagSetId,
            SpaceId     = set.SpaceId,
            Name        = name,
            Color       = color,
            Description = description,
            SortOrder   = maxSort + 1,
        };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return tag;
    }

    public async Task<Tag?> UpdateTagAsync(
        Guid id,
        string name,
        string? color,
        string? description,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
        if (tag is null)
        {
            return null;
        }

        if (await db.Tags.AnyAsync(
                t => t.TagSetId == tag.TagSetId && t.Name == name && t.Id != id, ct)
            .ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Tag '{name}' already exists in this set.");
        }

        tag.Name        = name;
        tag.Color       = color;
        tag.Description = description;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return tag;
    }

    /// <summary>Deletes a tag; its applications cascade at the DB level.</summary>
    public async Task<bool> DeleteTagAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
        if (tag is null)
        {
            return false;
        }

        db.Tags.Remove(tag);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Rewrites the set's tag ordering to match <paramref name="orderedTagIds"/>
    /// (ids not listed keep their relative order after the listed ones).</summary>
    public async Task ReorderTagsAsync(
        Guid tagSetId, IReadOnlyList<Guid> orderedTagIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedTagIds);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var tags = await db.Tags
            .Where(t => t.TagSetId == tagSetId)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .ToListAsync(ct).ConfigureAwait(false);

        var next = 0;
        foreach (var id in orderedTagIds)
        {
            // C# 14 null-conditional assignment: RHS (and the increment) only
            // evaluates when the tag was found.
            tags.FirstOrDefault(t => t.Id == id)?.SortOrder = next++;
        }
        foreach (var tag in tags.Where(t => !orderedTagIds.Contains(t.Id)))
        {
            tag.SortOrder = next++;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ── Applications ─────────────────────────────────────────────────────────

    /// <summary>All tag applications of one entity, with Tag + TagSet loaded,
    /// ordered by set then tag display order.</summary>
    public async Task<List<TagApplication>> GetForEntityAsync(
        TaggableEntityKind kind, Guid entityId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TagApplications
            .Include(a => a.TagSet)
            .Include(a => a.Tag)
            .Where(a => a.EntityKind == kind && a.EntityId == entityId)
            .OrderBy(a => a.TagSet.SortOrder).ThenBy(a => a.TagSet.Name)
            .ThenBy(a => a.Tag != null ? a.Tag.SortOrder : 0)
            .ToListAsync(ct);
    }

    /// <summary>Ids of entities of <paramref name="kind"/> that have at least
    /// one of <paramref name="tagIds"/> applied — any-match semantics. Powers
    /// the deploy dialog's tag filter over deployment targets.</summary>
    public async Task<HashSet<Guid>> GetEntityIdsWithAnyTagAsync(
        TaggableEntityKind kind,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tagIds);
        if (tagIds.Count == 0)
        {
            return [];
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.TagApplications
            .Where(a => a.EntityKind == kind &&
                        a.TagId != null &&
                        tagIds.Contains(a.TagId.Value))
            .Select(a => a.EntityId)
            .Distinct()
            .ToListAsync(ct);
        return [.. ids];
    }

    /// <summary>
    /// Replaces the entity's applied tags from ONE select-type set with
    /// <paramref name="tagIds"/> (diff-based; the editor's per-set save).
    /// Validates scope membership, set type, tag ownership, and SingleSelect
    /// cardinality.
    /// </summary>
    public async Task SetAppliedTagsAsync(
        Guid tagSetId,
        TaggableEntityKind kind,
        Guid entityId,
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tagIds);

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var set = await db.TagSets
            .Include(s => s.Tags)
            .FirstOrDefaultAsync(s => s.Id == tagSetId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tag set {tagSetId} not found.");

        if (set.Type == TagSetType.FreeText)
        {
            throw new InvalidOperationException(
                $"'{set.Name}' is a free-text set — use SetFreeTextValueAsync.");
        }
        EnsureScoped(set, kind);

        var distinct = tagIds.Distinct().ToList();
        if (set.Type == TagSetType.SingleSelect && distinct.Count > 1)
        {
            throw new InvalidOperationException(
                $"'{set.Name}' is single-select — at most one tag can be applied.");
        }

        var known = set.Tags.Select(t => t.Id).ToHashSet();
        var unknown = distinct.Where(id => !known.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"Tag(s) {string.Join(", ", unknown)} do not belong to set '{set.Name}'.");
        }

        var existing = await db.TagApplications
            .Where(a => a.TagSetId == tagSetId && a.EntityKind == kind && a.EntityId == entityId)
            .ToListAsync(ct).ConfigureAwait(false);

        db.TagApplications.RemoveRange(
            existing.Where(a => a.TagId is null || !distinct.Contains(a.TagId.Value)));

        var present = existing
            .Where(a => a.TagId is not null)
            .Select(a => a.TagId!.Value)
            .ToHashSet();
        foreach (var tagId in distinct.Where(id => !present.Contains(id)))
        {
            db.TagApplications.Add(new TagApplication
            {
                SpaceId    = set.SpaceId,
                TagSetId   = tagSetId,
                TagId      = tagId,
                EntityKind = kind,
                EntityId   = entityId,
                SetType    = set.Type,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sets (or clears, when null/whitespace) the entity's free-text
    /// value for one FreeText set — one value per set per entity.</summary>
    public async Task SetFreeTextValueAsync(
        Guid tagSetId,
        TaggableEntityKind kind,
        Guid entityId,
        string? value,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var set = await db.TagSets.FirstOrDefaultAsync(s => s.Id == tagSetId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tag set {tagSetId} not found.");

        if (set.Type != TagSetType.FreeText)
        {
            throw new InvalidOperationException(
                $"'{set.Name}' is not a free-text set — use SetAppliedTagsAsync.");
        }
        EnsureScoped(set, kind);

        var existing = await db.TagApplications
            .FirstOrDefaultAsync(
                a => a.TagSetId == tagSetId && a.EntityKind == kind && a.EntityId == entityId, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(value))
        {
            if (existing is not null)
            {
                db.TagApplications.Remove(existing);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            return;
        }

        if (existing is not null)
        {
            existing.FreeTextValue = value.Trim();
        }
        else
        {
            db.TagApplications.Add(new TagApplication
            {
                SpaceId       = set.SpaceId,
                TagSetId      = tagSetId,
                EntityKind    = kind,
                EntityId      = entityId,
                FreeTextValue = value.Trim(),
                SetType       = TagSetType.FreeText,
            });
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Canonical "TagSetName/TagName" (or "TagSetName/value" for
    /// free-text) strings of one tenant's applied tags — feeds the
    /// <c>Octopus.Deployment.Tenant.Tags</c> system variable. Static + context
    /// param so the workers' static plan-builder path can call it too.</summary>
    public static async Task<List<string>> GetTenantTagCanonicalsAsync(
        KrakenDbContext db, Guid tenantId, CancellationToken ct = default)
    {
        var rows = await db.TagApplications
            .Where(a => a.EntityKind == TaggableEntityKind.Tenant && a.EntityId == tenantId)
            .Select(a => new
            {
                SetName  = a.TagSet.Name,
                SetSort  = a.TagSet.SortOrder,
                TagName  = a.Tag != null ? a.Tag.Name : null,
                TagSort  = a.Tag != null ? a.Tag.SortOrder : 0,
                a.FreeTextValue,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .OrderBy(r => r.SetSort).ThenBy(r => r.SetName).ThenBy(r => r.TagSort)
            // Drop rows with no value on the SOURCE fields, not via an
            // ends-with-'/' proxy (which would wrongly discard a legitimate
            // free-text value or tag name that itself ends in '/').
            .Where(r => !string.IsNullOrEmpty(r.TagName ?? r.FreeTextValue))
            .Select(r => TagCanonical.Format(r.SetName, (r.TagName ?? r.FreeTextValue)!))
            .ToList();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void ValidateScopes(IReadOnlyCollection<TaggableEntityKind> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Count == 0)
        {
            throw new InvalidOperationException("A tag set needs at least one scope.");
        }
    }

    private static void EnsureScoped(TagSet set, TaggableEntityKind kind)
    {
        if (!set.Scopes.Contains(kind))
        {
            throw new InvalidOperationException(
                $"Tag set '{set.Name}' is not scoped to {kind} " +
                $"(scopes: {string.Join(", ", set.Scopes)}).");
        }
    }
}
