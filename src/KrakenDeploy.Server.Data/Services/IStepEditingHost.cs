using KrakenDeploy.Server.Core.Domain.Processes;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// M15 follow-up — the editor's view of "where do steps live and how do
/// I create / update / list them?" Implemented by both
/// <see cref="ProcessService"/> (deployment processes) and
/// <see cref="RunbookService"/> (runbook processes) so the unified
/// <c>StepFormDialog</c> works for both surfaces without conditional
/// type-dispatching.
///
/// <para>
/// <strong>Container</strong> = the row that owns a process tree —
/// <c>Project.Id</c> for deployment processes, <c>Runbook.Id</c> for
/// runbooks. The dialog passes containerId on Add; on Edit it works
/// from the step's own <see cref="IComposableStep.ProcessId"/>.
/// </para>
///
/// <para>
/// <strong>Runbook caveats:</strong> runbook steps don't carry the M14
/// execution knobs (Condition / Required / Retries / Timeout /
/// StartTrigger). <see cref="SupportsExecutionKnobs"/> tells the dialog
/// to hide the corresponding card on the runbook editor. The
/// <c>knobs</c> parameter on <see cref="UpdateStepAsync"/> /
/// <see cref="AddStepAsync"/> is ignored by the runbook implementation;
/// passing it is harmless.
/// </para>
/// </summary>
public interface IStepEditingHost
{
    /// <summary>True when the host's step entity carries the M14
    /// execution knobs (process steps do, runbook steps don't). Drives
    /// the visibility of the Execution card on <c>StepFormDialog</c>.</summary>
    bool SupportsExecutionKnobs { get; }

    /// <summary>
    /// Creates a new step. <paramref name="containerId"/> is the
    /// project id for processes, the runbook id for runbooks.
    /// Returns the new step's id so the caller can wire follow-up
    /// operations without an extra load.
    /// </summary>
    Task<Guid> AddStepAsync(
        Guid containerId,
        string name,
        string stepType,
        string packageId,
        List<string> targetRoles,
        Dictionary<string, string> config,
        string? stepPackageName,
        string? stepPackageVersion,
        StepExecutionKnobs? knobs,
        Guid? parentStepId,
        CancellationToken ct);

    /// <summary>
    /// Updates an existing step. Throws
    /// <see cref="ProcessValidationException"/> when the resulting
    /// process tree violates a validator invariant.
    /// </summary>
    Task UpdateStepAsync(
        Guid stepId,
        string name,
        string packageId,
        List<string> targetRoles,
        Dictionary<string, string> config,
        string? stepPackageName,
        string? stepPackageVersion,
        StepExecutionKnobs? knobs,
        UpdateParent? updateParent,
        CancellationToken ct);

    /// <summary>
    /// Returns every step in the process identified by
    /// <paramref name="processId"/> as <see cref="IComposableStep"/> so
    /// the editor's Parent dropdown can filter for
    /// <see cref="KrakenStepTypes.StepGroup"/> + exclude the edited
    /// step's own descendants (cycle prevention).
    /// </summary>
    Task<IReadOnlyList<IComposableStep>> GetProcessStepsAsync(
        Guid processId, CancellationToken ct);

    /// <summary>
    /// Resolves the project id for variable lookups (e.g. the ForEach
    /// Collection autocomplete over <c>StringArray</c> variables).
    ///
    /// <para>
    /// Exactly one of <paramref name="containerId"/> /
    /// <paramref name="processId"/> is expected to be set: containerId
    /// on Add (we already know the container directly), processId on
    /// Edit (we have to walk the snapshot tree back to the owning
    /// process row). Returns null when the lookup fails (e.g. the
    /// runbook was deleted under us); the editor falls back to an
    /// empty autocomplete suggestion list.
    /// </para>
    /// </summary>
    Task<Guid?> ResolveProjectIdAsync(
        Guid? containerId, Guid? processId, CancellationToken ct);
}
