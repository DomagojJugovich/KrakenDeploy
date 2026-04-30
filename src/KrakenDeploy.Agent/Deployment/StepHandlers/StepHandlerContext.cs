using KrakenDeploy.Contracts;

namespace KrakenDeploy.Agent.Deployment.StepHandlers;

/// <summary>
/// Contextual data passed to an <see cref="IStepHandler"/> when it executes.
/// </summary>
public sealed class StepHandlerContext
{
    /// <summary>The full deployment plan (environment, variables, all steps).</summary>
    public required DeploymentPlan Plan { get; init; }

    /// <summary>The specific step being executed.</summary>
    public required DeploymentStepPlan Step { get; init; }

    /// <summary>
    /// Path to the directory where the step's package was extracted.
    /// Empty string when <see cref="IStepHandler.RequiresPackage"/> is <c>false</c>.
    /// </summary>
    public required string ExtractDir { get; init; }

    /// <summary>
    /// Callback for appending a log line to the server.
    /// First argument is the log level ("info", "warning", "error").
    /// Second argument is the message text.
    /// </summary>
    public required Func<string, string, Task> LogAsync { get; init; }
}
