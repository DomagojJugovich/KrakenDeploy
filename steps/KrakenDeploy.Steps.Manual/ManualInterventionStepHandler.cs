using KrakenDeploy.Contracts.Steps;
using Octostache;

namespace KrakenDeploy.Steps.Manual;

/// <summary>
/// Step config keys for an <c>Octopus.Manual</c> step. The canonical definitions
/// live in <see cref="ManualInterventionConfigKeys"/> (shared with the server
/// orchestrator and the step-schema UI); these aliases keep the step package's
/// long-standing public surface stable.
/// </summary>
public static class OctopusManualConfigKeys
{
    /// <inheritdoc cref="ManualInterventionConfigKeys.Instructions"/>
    public const string Instructions = ManualInterventionConfigKeys.Instructions;

    /// <inheritdoc cref="ManualInterventionConfigKeys.ResponsibleTeamIds"/>
    public const string ResponsibleTeamIds = ManualInterventionConfigKeys.ResponsibleTeamIds;

    /// <inheritdoc cref="ManualInterventionConfigKeys.BlockConcurrentDeployments"/>
    public const string BlockConcurrentDeployments =
        ManualInterventionConfigKeys.BlockConcurrentDeployments;

    /// <inheritdoc cref="ManualInterventionConfigKeys.TimeoutHours"/>
    public const string TimeoutHours = ManualInterventionConfigKeys.TimeoutHours;

    /// <inheritdoc cref="ManualInterventionConfigKeys.LegacyInstructionsKey"/>
    public const string LegacyInstructionsKey = ManualInterventionConfigKeys.LegacyInstructionsKey;
}

/// <summary>
/// Handles <c>Octopus.Manual</c> for runners that have NO server to ask.
/// <para>
/// <strong>This is not the approval gate.</strong> Since WP3, an online task pauses
/// at a manual-intervention step and waits for a real human decision — that flow is
/// entirely server-side (<c>Octopus.Manual</c> is in
/// <c>WavePartitioner.ServerOnlyStepTypes</c>, so the step never reaches an agent
/// online). Offline drop bundles are REFUSED at bundle-generation time when the
/// process contains one (<c>OfflineDropBundleBuilder</c>), because an air-gapped box
/// cannot ask anybody either.
/// </para>
/// <para>
/// So this handler is only reachable by a runner executing a hand-built plan that
/// bypassed both gates. It cannot block — there is no approver to reach — so it
/// proceeds, but it logs a WARNING that the change-control gate was NOT enforced.
/// That line is the audit trail's only signal that an approval step was passed
/// without an approval, which for a state-sector deployment is exactly what a
/// reviewer needs to see.
/// </para>
/// <para>
/// Property contract is mirrored verbatim from
/// <a href="https://octopus.com/docs/projects/built-in-step-templates/manual-intervention-and-approvals">Octopus public docs</a>
/// (clean-room — see <c>docs/architecture.md#step-execution-model</c>).
/// </para>
/// </summary>
public sealed class ManualInterventionStepHandler : IStepHandler
{
    public bool CanHandle(string stepType)
        => stepType.Equals(ManualInterventionConfigKeys.StepType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Manual intervention steps do not require a package — they are purely informational.
    /// </summary>
    public bool RequiresPackage => false;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        // Octopus contract first, legacy Kraken key second.
        var rawInstructions = context.Step.Config.GetValueOrDefault(
            ManualInterventionConfigKeys.Instructions);
        if (string.IsNullOrWhiteSpace(rawInstructions))
        {
            rawInstructions = context.Step.Config.GetValueOrDefault(
                ManualInterventionConfigKeys.LegacyInstructionsKey);
        }

        // Resolve #{...} placeholders so messages referencing project /
        // environment / variable names show their real values to the operator.
        var instructions = string.IsNullOrWhiteSpace(rawInstructions)
            ? null
            : BuildOctostache(context.Plan.Variables).Evaluate(rawInstructions);

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            await context.LogAsync("info",
                $"Manual intervention instructions: {instructions}").ConfigureAwait(false);
        }
        else
        {
            await context.LogAsync("info",
                "Manual intervention step (no instructions provided).")
                .ConfigureAwait(false);
        }

        // Surface team scope + block-concurrent flag for audit-log clarity even
        // though this runner has no approver to route them to.
        var teamIds = ManualInterventionConfigKeys.ParseTeamTokens(
            context.Step.Config.GetValueOrDefault(
                ManualInterventionConfigKeys.ResponsibleTeamIds));
        if (teamIds.Count > 0)
        {
            await context.LogAsync("info",
                $"Responsible team(s) per source process: {string.Join(", ", teamIds)}.")
                .ConfigureAwait(false);
        }

        if (ManualInterventionConfigKeys.ParseBool(context.Step.Config.GetValueOrDefault(
                ManualInterventionConfigKeys.BlockConcurrentDeployments)))
        {
            await context.LogAsync("info",
                "Source process requested BlockConcurrentDeployments=True — Kraken " +
                "serializes deployments per (project, environment, tenant) " +
                "unconditionally, so this flag adds nothing.")
                .ConfigureAwait(false);
        }

        // The load-bearing line: an approval gate was passed with no approval.
        await context.LogAsync("warning",
            "APPROVAL NOT ENFORCED: this runner has no server to ask, so the manual " +
            "intervention was passed without a human decision. Online tasks pause at " +
            "this step and require an approver; offline drop bundles containing it are " +
            "refused at generation time. If you are seeing this line in a production " +
            "deployment log, the change-control gate did not run.")
            .ConfigureAwait(false);

        return true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static VariableDictionary BuildOctostache(IReadOnlyDictionary<string, string> variables)
    {
        var dict = new VariableDictionary();
        foreach (var (k, v) in variables)
        {
            dict.Set(k, v);
        }
        return dict;
    }
}
