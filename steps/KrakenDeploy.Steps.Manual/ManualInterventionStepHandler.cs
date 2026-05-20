using KrakenDeploy.Contracts.Steps;
using Octostache;

namespace KrakenDeploy.Steps.Manual;

/// <summary>
/// Step config keys for an <c>Octopus.Manual</c> step, mirroring Octopus's
/// <c>Octopus.Action.Manual.*</c> namespace exactly. Sourced from
/// <a href="https://octopus.com/docs/projects/built-in-step-templates/manual-intervention-and-approvals">Octopus public docs</a>
/// (clean-room — not from Calamari source; see docs/architecture.md#step-execution-model).
/// </summary>
public static class OctopusManualConfigKeys
{
    private const string Prefix = "Octopus.Action.Manual.";

    /// <summary>
    /// Required (in Octopus UI). Markdown-formatted instructions shown to the
    /// human approver. Octostache <c>#{...}</c> placeholders are resolved at
    /// handler time so messages can reference deployment / environment / project
    /// variables.
    /// </summary>
    public const string Instructions = Prefix + "Instructions";

    /// <summary>
    /// Optional. Identifiers of the team(s) authorised to resolve the
    /// intervention. Octopus's serialiser uses commas or semicolons; the
    /// handler tolerates both on read. If empty, "anybody with permission
    /// to deploy the project can perform the manual intervention" (per the
    /// Octopus docs).
    /// </summary>
    public const string ResponsibleTeamIds = Prefix + "ResponsibleTeamIds";

    /// <summary>
    /// Optional. When <c>True</c>, Octopus blocks other deployments of the
    /// same project from progressing past this step until the intervention
    /// is resolved. Kraken runs unattended and auto-approves, so this is
    /// informational only — surfaced in the deploy log for audit.
    /// </summary>
    public const string BlockConcurrentDeployments = Prefix + "BlockConcurrentDeployments";

    /// <summary>
    /// Legacy Kraken key used by an earlier internal step-template before
    /// alignment with the Octopus contract. Honoured on read for back-compat
    /// with any process already authored against the old shape.
    /// </summary>
    public const string LegacyInstructionsKey = "Instructions";
}

/// <summary>
/// Handles <c>Octopus.Manual</c> step type — the canonical step-package
/// implementation (Phase D-8). Identical behaviour to the legacy in-DI
/// <c>KrakenDeploy.Agent.Deployment.StepHandlers.ManualInterventionStepHandler</c>;
/// once Phase D-8 wraps and every built-in is package-backed, the in-DI
/// version is retired.
/// <para>
/// In a fully automated pipeline, manual intervention steps are automatically
/// approved after logging the step instructions, the responsible teams, and
/// the BlockConcurrentDeployments flag (informational only — Kraken doesn't
/// gate other deployments). This ensures that step templates imported from
/// the Octopus Library or a real Octopus deploymentprocess that include a
/// manual approval gate do not block an unattended deployment.
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
        => stepType.Equals("Octopus.Manual", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Manual intervention steps do not require a package — they are purely informational.
    /// </summary>
    public bool RequiresPackage => false;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        // Octopus contract first, legacy Kraken key second.
        var rawInstructions = context.Step.Config.GetValueOrDefault(
            OctopusManualConfigKeys.Instructions);
        if (string.IsNullOrWhiteSpace(rawInstructions))
        {
            rawInstructions = context.Step.Config.GetValueOrDefault(
                OctopusManualConfigKeys.LegacyInstructionsKey);
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
        // though Kraken's auto-approve path ignores them at runtime.
        var teamIds = ParseTeamIds(context.Step.Config.GetValueOrDefault(
            OctopusManualConfigKeys.ResponsibleTeamIds));
        if (teamIds.Count > 0)
        {
            await context.LogAsync("info",
                $"Responsible team(s) per source process: {string.Join(", ", teamIds)}.")
                .ConfigureAwait(false);
        }

        if (ParseBool(context.Step.Config.GetValueOrDefault(
                OctopusManualConfigKeys.BlockConcurrentDeployments)))
        {
            await context.LogAsync("info",
                "Source process requested BlockConcurrentDeployments=True " +
                "— Kraken runs unattended and does not gate concurrent deployments. "
                + "Honoured by attended-mode Octopus only.")
                .ConfigureAwait(false);
        }

        await context.LogAsync("info",
            "Step auto-approved (unattended deployment mode).").ConfigureAwait(false);

        return true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static List<string> ParseTeamIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }
        // Octopus's serialiser uses commas / semicolons interchangeably depending
        // on the surface (UI vs API); tolerate either on read.
        return [.. raw.Split(
            [',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static bool ParseBool(string? value)
        => value is not null && value.Equals("True", StringComparison.OrdinalIgnoreCase);

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
