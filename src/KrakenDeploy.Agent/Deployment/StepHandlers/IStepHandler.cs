namespace KrakenDeploy.Agent.Deployment.StepHandlers;

/// <summary>
/// Pluggable handler for a specific deployment step type.
/// Register all implementations with the DI container; <see cref="DeploymentExecutor"/>
/// resolves the first handler that returns <c>true</c> from <see cref="CanHandle"/>.
/// </summary>
public interface IStepHandler
{
    /// <summary>
    /// Returns <c>true</c> when this handler can execute the given <paramref name="stepType"/>.
    /// </summary>
    bool CanHandle(string stepType);

    /// <summary>
    /// <c>true</c> if the handler requires the step's package to be downloaded and
    /// extracted before <see cref="HandleAsync"/> is called.
    /// <c>false</c> for step types that operate independently of a package
    /// (e.g. <c>Octopus.Manual</c>).
    /// </summary>
    bool RequiresPackage { get; }

    /// <summary>
    /// Executes the step.
    /// Returns <c>true</c> on success, <c>false</c> on failure.
    /// Any exception thrown will be caught by the executor and treated as a failure.
    /// </summary>
    Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct);
}
