namespace KrakenDeploy.Agent.Deployment.StepHandlers;

/// <summary>
/// Handles <c>Octopus.Manual</c> step type.
/// <para>
/// In a fully automated pipeline, manual intervention steps are automatically
/// approved after logging the step instructions.  This ensures that step templates
/// imported from the Octopus Library that include a manual approval gate do not
/// block an unattended deployment.
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
        context.Step.Config.TryGetValue("Instructions", out var instructions);

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

        await context.LogAsync("info",
            "Step auto-approved (unattended deployment mode).").ConfigureAwait(false);

        return true;
    }
}
