using System.Globalization;
using KrakenDeploy.Contracts.Steps;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// Outcome of resolving a manual-intervention step's configured approvers.
/// <see cref="Error"/> is non-null when the configuration is unusable — and it must be
/// treated as a REFUSAL, never as "no restriction": an empty approver list means
/// "anyone holding the respond permission", so degrading to it would silently widen the
/// approver set, which is the one failure mode this whole path exists to prevent.
/// </summary>
public readonly record struct ResponsibleTeamResolution(
    Guid[] TeamIds,
    string[] TeamNames,
    string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// WP3 — the single authority for turning a manual-intervention step's
/// <c>Octopus.Action.Manual.*</c> configuration into a validated approver set.
/// <para>
/// Shared deliberately by the two places that need the same answer: the orchestrator's
/// gate (at pause time, where a bad configuration must fail the task) and
/// <see cref="ProcessService"/> (at save time, where it must refuse the edit). Those
/// rules are security-relevant — an unresolvable id or an "Everyone" team must never
/// degrade to an unrestricted gate — so they live in ONE place rather than being
/// reimplemented per call site, where they would drift.
/// </para>
/// <para>
/// Validating at save is the earlier feedback; validating at pause remains the
/// fail-closed backstop, because a process can also arrive by REST or by import without
/// ever passing through the editor.
/// </para>
/// </summary>
public static class ResponsibleTeamResolver
{
    /// <summary>
    /// Resolves the step's configured responsible teams against the teams visible in
    /// <paramref name="spaceId"/>.
    /// <list type="bullet">
    ///   <item>Reads the config key case-INSENSITIVELY — a casing miss would otherwise
    ///     yield an empty list, i.e. fail OPEN.</item>
    ///   <item>A token that is not a GUID, or a GUID that resolves to no team visible in
    ///     this Space, is an ERROR (a process imported from Octopus carries Octopus team
    ///     ids like <c>teams-123</c>).</item>
    ///   <item>An "Everyone" team is an ERROR: every authenticated user belongs to it, so
    ///     naming it reports a restriction that restricts nobody.</item>
    ///   <item>A Space-scoped team of ANOTHER Space is invisible here, so it resolves to
    ///     nothing and errors.</item>
    /// </list>
    /// </summary>
    public static async Task<ResponsibleTeamResolution> ResolveAsync(
        KrakenDbContext db,
        Guid spaceId,
        string stepName,
        IReadOnlyDictionary<string, string> config,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(config);

        var tokens = ManualInterventionConfigKeys.ParseTeamTokens(
            ManualInterventionConfigKeys.Read(
                config, ManualInterventionConfigKeys.ResponsibleTeamIds));
        if (tokens.Count == 0)
        {
            return new ResponsibleTeamResolution([], [], null);
        }

        var requested = new List<Guid>(tokens.Count);
        var unresolved = new List<string>();
        foreach (var token in tokens)
        {
            if (Guid.TryParse(token, out var id))
            {
                requested.Add(id);
            }
            else
            {
                unresolved.Add(token);
            }
        }

        var known = requested.Count == 0
            ? []
            : await db.Teams
                // Team carries NO global query filter (it is not ISpaceScoped) — Space
                // visibility here is exactly this explicit predicate: system teams
                // (SpaceId == null) are visible everywhere; a Space-scoped team is only
                // visible in its own Space.
                .Where(t => requested.Contains(t.Id)
                         && (t.SpaceId == null || t.SpaceId == spaceId))
                .Select(t => new { t.Id, t.Name, t.IsEveryoneTeam })
                .ToListAsync(ct)
                .ConfigureAwait(false);

        unresolved.AddRange(
            requested.Except(known.Select(t => t.Id)).Select(id => id.ToString()));

        if (unresolved.Count > 0)
        {
            return new ResponsibleTeamResolution([], [],
                $"Manual intervention step '{stepName}' lists responsible team(s) that do " +
                $"not resolve to a team in this Space: {string.Join(", ", unresolved)}. " +
                "Ignoring them would let ANYONE with the approve permission respond " +
                "instead of just those teams, so the configuration is refused. (A process " +
                "imported from Octopus carries Octopus team ids and must be re-pointed at " +
                "Kraken teams.)");
        }

        var vacuous = known.Where(t => t.IsEveryoneTeam).Select(t => t.Name).ToArray();
        if (vacuous.Length > 0)
        {
            return new ResponsibleTeamResolution([], [],
                $"Manual intervention step '{stepName}' names the " +
                $"'{string.Join("', '", vacuous)}' team as responsible, but every " +
                "authenticated user belongs to it, so it restricts nobody. Name a real " +
                "team, or clear the field to let anyone holding the approve permission " +
                "respond.");
        }

        return new ResponsibleTeamResolution(
            [.. known.Select(t => t.Id)],
            [.. known.Select(t => t.Name)],
            null);
    }

    /// <summary>
    /// Whether a step type is the manual-intervention gate. Exposed so a caller can skip
    /// resolving a Space id it would otherwise need only to run this validation — which
    /// is every step save in the product except the rare gate.
    /// </summary>
    public static bool IsGateStep(string? stepType)
        => stepType is not null
           && stepType.Equals(
                  ManualInterventionConfigKeys.StepType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <see cref="ValidateStepConfigAsync"/> as a guard: throws
    /// <see cref="ArgumentException"/> when the configuration is unusable, so a save path
    /// can simply call it.
    /// <para>
    /// The single guard for EVERY write path (WP3-b). It started as two private helpers on
    /// <c>ProcessService</c>, which meant the sibling <c>RunbookService</c> and the process
    /// IMPORT path silently accepted gate config nobody validated — and import is the most
    /// likely producer of unresolvable approver ids, since an Octopus process carries
    /// Octopus team ids.
    /// </para>
    /// <para>
    /// <paramref name="spaceId"/> is nullable on purpose: a caller that cannot resolve the
    /// owning Space cannot validate approver visibility either, and must refuse rather than
    /// fall through with <c>Guid.Empty</c> — which matches only system teams and reports a
    /// legitimate Space-scoped approver as unresolvable.
    /// </para>
    /// </summary>
    public static async Task EnsureStepConfigValidAsync(
        KrakenDbContext db,
        Guid? spaceId,
        string stepType,
        string stepName,
        IReadOnlyDictionary<string, string> config,
        CancellationToken ct = default)
    {
        if (!IsGateStep(stepType))
        {
            return;
        }
        if (spaceId is not { } sid || sid == Guid.Empty)
        {
            throw new ArgumentException(
                $"Manual intervention step '{stepName}' cannot be validated because its " +
                "owning Space could not be resolved, so there is no way to tell which " +
                "teams may answer it. This usually means the parent project or runbook no " +
                "longer exists.", nameof(config));
        }
        var error = await ValidateStepConfigAsync(db, sid, stepType, stepName, config, ct)
            .ConfigureAwait(false);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(config));
        }
    }

    /// <summary>
    /// Validates the whole manual-intervention configuration for a step being SAVED.
    /// Returns <c>null</c> when it is usable, else an operator-readable reason.
    /// <para>
    /// Covers the timeout too: the field is free text, and an unparseable value silently
    /// falls back to the engine default at run time — so an operator who typed
    /// <c>0,5</c> meaning thirty minutes would otherwise get 72 hours with no warning.
    /// </para>
    /// </summary>
    public static async Task<string?> ValidateStepConfigAsync(
        KrakenDbContext db,
        Guid spaceId,
        string stepType,
        string stepName,
        IReadOnlyDictionary<string, string> config,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!stepType.Equals(ManualInterventionConfigKeys.StepType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var teams = await ResolveAsync(db, spaceId, stepName, config, ct).ConfigureAwait(false);
        if (!teams.IsValid)
        {
            return teams.Error;
        }

        var rawTimeout = ManualInterventionConfigKeys.Read(
            config, ManualInterventionConfigKeys.TimeoutHours);
        if (!string.IsNullOrWhiteSpace(rawTimeout)
            && ManualInterventionConfigKeys.ParseTimeout(rawTimeout) is null)
        {
            // WP3-b: 0 lands here too, and deserves its own reason — it used to be
            // accepted as "wait forever" and is the one rejected value an operator may
            // have typed deliberately.
            var isZero = double.TryParse(rawTimeout, NumberStyles.Float,
                             CultureInfo.InvariantCulture, out var hours)
                         && hours == 0;
            var why = isZero
                ? "0 is not allowed: a gate with no expiry never times out, and a paused " +
                  "task holds its project + environment slot for as long as it waits — so " +
                  "one unanswered gate would block every later release of that project and " +
                  "environment until somebody cancelled the task. Give it a real deadline."
                : "Use a plain number of hours with a DOT decimal separator " +
                  "(0.5 = thirty minutes), or leave it blank for the server default. " +
                  "Left as-is it would silently fall back to the server default instead " +
                  "of the value you intended.";
            return $"Manual intervention step '{stepName}' has an unusable " +
                   $"\"Auto-fail after (hours)\" value ('{rawTimeout}'). Allowed range is " +
                   $"greater than 0 up to {ManualInterventionConfigKeys.MaxTimeoutHours}. " +
                   why;
        }

        return null;
    }
}
