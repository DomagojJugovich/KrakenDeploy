using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Variables;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// CRUD and scope-resolution for project <see cref="VariableSet"/>s and <see cref="Variable"/>s.
/// </summary>
public class VariableService(KrakenDbContext db, IEncryptionService encryption)
{
    // ── Set management ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the <see cref="VariableSet"/> for the project, creating one if it
    /// doesn't exist yet.
    /// </summary>
    public async Task<VariableSet> GetOrCreateSetAsync(
        Guid projectId, CancellationToken ct = default)
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

    // ── Variable CRUD ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns all variables for a project, with sensitive values redacted.
    /// </summary>
    public async Task<List<VariableDto>> GetVariablesAsync(
        Guid projectId, CancellationToken ct = default)
    {
        var set = await db.VariableSets
            .Include(vs => vs.Variables)
            .FirstOrDefaultAsync(vs => vs.ProjectId == projectId, ct)
            .ConfigureAwait(false);

        if (set is null)
        {
            return [];
        }

        return set.Variables
            .Select(v => new VariableDto(
                v.Id,
                v.Name,
                v.Type == VariableType.Sensitive ? "***" : v.Value,
                v.Type.ToString(),
                v.Scope))
            .ToList();
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
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        var set = await GetOrCreateSetAsync(projectId, ct).ConfigureAwait(false);

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
    /// </summary>
    public async Task<Variable?> UpdateVariableAsync(
        Guid id,
        string name,
        string value,
        VariableType type,
        VariableScope? scope,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        var variable = await db.Variables
            .FindAsync([id], ct)
            .ConfigureAwait(false);

        if (variable is null)
        {
            return null;
        }

        variable.Name = name;
        variable.Type = type;
        variable.Scope = scope ?? new VariableScope();
        variable.Value = type switch
        {
            VariableType.Sensitive => encryption.Encrypt(value),
            VariableType.StringArray => NormalizeStringArray(value),
            _ => value,
        };

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return variable;
    }

    /// <summary>
    /// Deletes a variable by ID. Returns <c>false</c> if not found.
    /// </summary>
    public async Task<bool> DeleteVariableAsync(Guid id, CancellationToken ct = default)
    {
        var variable = await db.Variables
            .FindAsync([id], ct)
            .ConfigureAwait(false);

        if (variable is null)
        {
            return false;
        }

        db.Variables.Remove(variable);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ── Scope resolution ───────────────────────────────────────────────────

    /// <summary>
    /// Resolves the effective variable set for a specific deployment context.
    /// <para>
    /// For each variable name, the most-specific matching value wins.
    /// Specificity is scored as: environment match (+4), role overlap (+2),
    /// target machine match (+1). Unscoped variables (score 0) are the fallback.
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
        CancellationToken ct = default)
    {
        var set = await db.VariableSets
            .Include(vs => vs.Variables)
            .AsNoTracking()
            .FirstOrDefaultAsync(vs => vs.ProjectId == projectId, ct)
            .ConfigureAwait(false);

        if (set is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var byName = set.Variables
            .GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byName)
        {
            var winner = group
                .Where(v => v.Scope.Matches(environmentId, targetId, targetRoles))
                .OrderByDescending(v => v.Scope.SpecificityScore())
                .FirstOrDefault();

            if (winner is null)
            {
                continue;
            }

            result[winner.Name] = winner.Type == VariableType.Sensitive
                ? encryption.Decrypt(winner.Value)
                : winner.Value;  // String: plain; StringArray: JSON string
        }

        return result;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

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
