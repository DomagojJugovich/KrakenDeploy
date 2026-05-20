namespace KrakenDeploy.Contracts.Steps;

/// <summary>
/// Pluggable handler for a specific deployment step type.
/// <para>
/// Implementations live in step-package projects (Phase D-8) and are loaded
/// at runtime by the agent's <c>StepPackageLoader</c>. The loader expects a
/// public, parameterless constructor; the executor disposes the instance
/// after <see cref="HandleAsync"/> returns when it implements
/// <see cref="System.IDisposable"/>. Lifecycle is per-step-execution.
/// </para>
/// <para>
/// This type is part of the stable SDK surface — see <c>docs/sdk-surface.md</c>
/// for the compatibility contract.
/// </para>
/// </summary>
public interface IStepHandler
{
    /// <summary>
    /// Returns <c>true</c> when this handler can execute the given
    /// <paramref name="stepType"/>. The executor calls this on every handler
    /// in priority order and picks the first one that returns <c>true</c>.
    /// A handler may match multiple step types (e.g. <c>Kraken.Script</c> +
    /// <c>Octopus.Script</c>).
    /// </summary>
    bool CanHandle(string stepType);

    /// <summary>
    /// <c>true</c> if the handler requires the step's primary package to be
    /// downloaded and extracted before <see cref="HandleAsync"/> is called.
    /// <c>false</c> for step types that operate independently of a package
    /// (e.g. <c>Octopus.Manual</c>, <c>Octopus.Script</c> with inline body).
    /// </summary>
    bool RequiresPackage { get; }

    /// <summary>
    /// Executes the step. Returns <c>true</c> on success, <c>false</c> on
    /// failure. Any exception thrown is caught by the executor and treated
    /// as a failure with the exception's <c>Message</c> surfaced to the
    /// deployment log.
    /// </summary>
    Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct);
}
