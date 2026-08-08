using System.Text.Json;
using System.Text.Json.Serialization;
using KrakenDeploy.Server.Core.Domain.Variables;

namespace KrakenDeploy.Server.Transport;

/// <summary>
/// WP3 — everything the wave loop carries in memory that a resumed orchestration
/// cannot recompute from the frozen process snapshot. Written when a task pauses
/// at a manual-intervention gate, read back when it resumes.
/// <para>
/// The pause deliberately does NOT park the orchestration in-process: the worker
/// returns, freeing its <c>NodeTaskGate</c> slot and dropping its lease, because a
/// 72-hour approval window reliably spans a server restart. That makes this record
/// the ONLY link between the two halves of the run — anything missing here is
/// silently lost work, so every field below is load-bearing.
/// </para>
/// <para>
/// What is deliberately NOT here, because <c>DispatchCoreAsync</c> rebuilds it:
/// the target set, the per-target dispatch contexts, the flattened plan and the
/// wave partitioning. For a deployment that rebuild is deterministic (frozen
/// <c>ProcessSnapshot</c> + <c>VariableSnapshot</c>); for a runbook run the process
/// is frozen but variables resolve LIVE, so a run paused across a variable edit
/// resumes with the new values — the documented runbook variable contract, not a
/// pause-specific defect.
/// </para>
/// </summary>
internal sealed record TaskPauseCheckpoint
{
    /// <summary>Index into the re-partitioned wave list to restart at — the wave
    /// holding the manual-intervention step that caused the pause.</summary>
    public int ResumeWaveIndex { get; init; }

    /// <summary>The deployment-global failing flag. Drives
    /// <c>Condition=Success/Failure/Always</c> evaluation for every later wave, so
    /// losing it would silently run cleanup steps as if nothing had failed.</summary>
    public bool HasFailed { get; init; }

    /// <summary>
    /// A manual-intervention gate on an EARLIER wave was rejected or timed out.
    /// Separate from <see cref="HasFailed"/> because only this input makes the
    /// terminal verdict <c>Failed</c> — <c>HasFailed</c> alone resolves
    /// <c>SucceededWithWarnings</c>, which is the wrong verdict for a refused change.
    /// <para>
    /// Load-bearing whenever a process has MORE THAN ONE gate: reject gate A on wave
    /// 2, pause at gate B on wave 5, approve B — without this field the run would
    /// finalise as a yellow-badge success even though a human refused it, and wave 2
    /// is never revisited so the flag cannot be recomputed.
    /// </para>
    /// </summary>
    public bool InterventionRejected { get; init; }

    /// <summary>Targets still eligible for later waves, in dispatch order. Drop-out
    /// is applied to the in-memory <c>aliveTargets</c> list only.</summary>
    public Guid[] AliveTargetIds { get; init; } = [];

    /// <summary>Targets that dropped out, with the detail
    /// <c>DeploymentTerminalStatusResolver</c> needs. The <c>TargetDropped</c> audit
    /// rows carry prose, not structured state, so they cannot substitute.</summary>
    public CheckpointDroppedTarget[] DroppedTargets { get; init; } = [];

    /// <summary>Surviving targets with a non-required failure — each skips only its
    /// OWN later <c>Condition=Success</c> steps (BestEffort isolation).</summary>
    public Guid[] SoftFailedTargetIds { get; init; } = [];

    /// <summary>
    /// Per-target accumulated output bags. NOT recoverable from
    /// <c>task_output_variables</c>: that table's key is (task, stepKey) with no
    /// target dimension, so it holds only the last-writer-wins fold.
    /// </summary>
    public CheckpointOutputBag[] TargetOutputs { get; init; } = [];

    /// <summary>The server-wave view: last-writer-wins across targets plus every
    /// server-step capture.</summary>
    public CheckpointOutputBag[] ServerOutputs { get; init; } = [];
}

/// <summary>One dropped target, flattened for the checkpoint. <c>Reason</c> is the
/// <c>DeploymentWorker.DropReason</c> name rather than its ordinal so a future
/// reordering of that private enum cannot silently reinterpret a live
/// checkpoint.</summary>
internal sealed record CheckpointDroppedTarget(
    Guid TargetId,
    string Reason,
    string? StepName,
    string? Error);

/// <summary>One step's captured outputs for one scope. <see cref="TargetId"/> is
/// null for the server view.</summary>
internal sealed record CheckpointOutputBag(
    Guid? TargetId,
    string StepKey,
    Dictionary<string, string> Values,
    string[] SensitiveMergedKeys);

/// <summary>
/// Serializes a <see cref="TaskPauseCheckpoint"/> to (and from) the encrypted
/// <c>server_tasks.pause_checkpoint_encrypted</c> column.
/// <para>
/// The payload is ENCRYPTED because the output bags embed captured sensitive values
/// in plaintext — the same reason <c>TaskOutputVariable.Value</c> is encrypted at
/// rest. A tampered or foreign-DEK payload throws out of
/// <see cref="Read"/> rather than degrading to an empty checkpoint: resuming a
/// deployment with silently-lost failure state is worse than failing it.
/// </para>
/// </summary>
internal static class TaskPauseCheckpointCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write(TaskPauseCheckpoint checkpoint, IEncryptionService encryption)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(encryption);
        return encryption.Encrypt(JsonSerializer.Serialize(checkpoint, Options));
    }

    /// <summary>
    /// Decrypts and deserializes a checkpoint.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The payload decrypted but did not deserialize to a checkpoint. Callers turn
    /// this into a task failure with an operator-readable reason — never into an
    /// empty checkpoint.
    /// </exception>
    public static TaskPauseCheckpoint Read(string payload, IEncryptionService encryption)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentNullException.ThrowIfNull(encryption);
        return JsonSerializer.Deserialize<TaskPauseCheckpoint>(
                   encryption.Decrypt(payload), Options)
               ?? throw new InvalidOperationException(
                   "Pause checkpoint payload deserialized to null.");
    }
}
