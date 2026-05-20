using KrakenDeploy.Contracts.Steps;

namespace KrakenStep;

/// <summary>
/// Sample <see cref="IStepHandler"/> that handles the
/// <c>STEP_TYPE_PLACEHOLDER</c> step type. Replace the body of
/// <see cref="HandleAsync"/> with your real deployment logic; everything
/// else is supporting metadata for the agent's <c>StepPackageLoader</c>.
/// <para>
/// Lifecycle: the agent creates a fresh instance per step execution via
/// <c>Activator.CreateInstance</c>, so the type must have a public
/// parameterless constructor and may NOT rely on DI. Use <c>new</c> for
/// any per-call helpers; do NOT cache state across calls — multiple
/// deployments can run concurrently, each with its own handler instance.
/// </para>
/// </summary>
public sealed class SampleStepHandler : IStepHandler
{
    public bool CanHandle(string stepType) =>
        stepType.Equals("STEP_TYPE_PLACEHOLDER", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>true</c> when the deployment executor should download + extract
    /// the step's primary package before calling <see cref="HandleAsync"/>.
    /// Flip to <c>false</c> for steps that don't deploy a package (notify,
    /// approve, run an external API, etc.) — your handler then runs
    /// directly against the agent's filesystem with no extract dir.
    /// </summary>
    public bool RequiresPackage => true;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        // The context exposes everything the executor knows about this step:
        //   - context.Plan          full DeploymentPlan (env name, variables, all steps)
        //   - context.Step          this DeploymentStepPlan (name, config, package version)
        //   - context.ExtractDir    where the extracted package sits (when RequiresPackage)
        //   - context.ArtifactsDir  write files here; the agent uploads them after Save
        //   - context.LogAsync      append a log line to the deployment (level, message)
        //
        // Read step-specific configuration out of context.Step.Config — the
        // values are already Octostache-substituted server-side, so
        // "#{Octopus.Environment.Name}" appears as e.g. "Production".
        var greeting = context.Step.Config.GetValueOrDefault("Greeting", "Hello");

        await context.LogAsync("info",
            $"{greeting} from {nameof(SampleStepHandler)} on environment '{context.Plan.EnvironmentName}'.")
            .ConfigureAwait(false);

        // Return true on success, false on a clean handled failure. Throw
        // for unhandled errors — the executor catches the exception and
        // surfaces its Message as the failure reason in the deployment log.
        return await Task.FromResult(true).ConfigureAwait(false);
    }
}
