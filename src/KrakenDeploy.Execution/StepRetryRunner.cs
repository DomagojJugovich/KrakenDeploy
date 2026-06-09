using System.Globalization;

namespace KrakenDeploy.Execution;

/// <summary>
/// Transport-agnostic per-step retry loop with an optional per-attempt
/// timeout. Shared by the server orchestrator (<c>DeploymentWorker</c>'s
/// server-step path) and the offline agent runner (<c>DeploymentExecutor</c>)
/// so retry counting, the clamps, the per-attempt linked-CTS timeout, and the
/// retry-marker wording have a single source of truth — online and offline
/// retry identically.
///
/// <para>
/// The loop owns only the DECISION logic. Each side keeps its own
/// side-effects (DB writes + audit on the server; <c>IServerLink</c> log
/// lines on the agent) by passing them through the callbacks. The runner
/// never touches a database, an audit log, or a transport.
/// </para>
///
/// <para>
/// Timeout semantics match the pre-extraction code exactly: a per-attempt
/// timeout is treated as a failed attempt and is therefore RETRIED if
/// attempts remain. A timeout fired by the outer <paramref name="ct"/>
/// (deployment-level cancel) is NOT swallowed — it propagates. Whether a
/// timed-out attempt is logged per-attempt (agent) or only the final one
/// is surfaced via <see cref="Outcome{TResult}.TimedOut"/> (server) is the
/// caller's choice, driven by which callbacks it supplies.
/// </para>
/// </summary>
public static class StepRetryRunner
{
    /// <summary>
    /// Result of the retry loop. <see cref="Result"/> is the final attempt's
    /// value (success or failure). <see cref="TimedOut"/> reflects the FINAL
    /// attempt only — callers that surface timeout once (server) read it here;
    /// callers that log every timed-out attempt do so via
    /// <c>onAttemptTimedOutAsync</c>. <see cref="AttemptCount"/> is the number
    /// of attempts made (1 = no retries).
    /// </summary>
    public sealed record Outcome<TResult>(TResult Result, bool TimedOut, int AttemptCount);

    /// <summary>
    /// Context handed to the retry callback before each retry delay. Carries
    /// the pre-formatted <see cref="Marker"/> (identical wording across server
    /// and agent) plus the structured fields the server's audit row needs.
    /// </summary>
    public readonly record struct RetryAttempt(
        int Attempt, int MaxAttempts, int DelaySeconds, string Marker);

    /// <summary>
    /// Runs <paramref name="runAttempt"/> with its per-attempt timeout, retried
    /// up to <paramref name="maxRetries"/> times (clamped to ≥ 0) with
    /// <paramref name="retryDelaySeconds"/> (clamped to ≥ 0) between attempts.
    /// </summary>
    /// <param name="stepName">Used only to format the retry marker.</param>
    /// <param name="maxRetries">Additional attempts after the first failure;
    /// negative values clamp to 0.</param>
    /// <param name="retryDelaySeconds">Delay between attempts; negative values
    /// clamp to 0.</param>
    /// <param name="timeoutSeconds">Per-attempt timeout; ≤ 0 means unlimited
    /// (no linked CTS is allocated).</param>
    /// <param name="runAttempt">Executes one attempt against the supplied token
    /// (the per-attempt linked token when a timeout is set).</param>
    /// <param name="isSuccess">Whether a result counts as success (stops the
    /// loop). A failed result with attempts remaining is retried.</param>
    /// <param name="onTimeoutResult">Produces the result value to use when an
    /// attempt times out (e.g. a failed outcome with an empty output bag).</param>
    /// <param name="onAttemptTimedOutAsync">Invoked (with the timeout in
    /// seconds) for EVERY timed-out attempt. Null when the caller surfaces the
    /// timeout once via <see cref="Outcome{TResult}.TimedOut"/> instead.</param>
    /// <param name="onRetryAsync">Invoked before each retry delay so the caller
    /// can log the marker + record an audit row.</param>
    /// <param name="onLateSuccessAsync">Invoked (with the attempt count) when a
    /// step succeeds after at least one retry. Null when the caller emits no
    /// late-success marker (agent).</param>
    public static async Task<Outcome<TResult>> RunAsync<TResult>(
        string stepName,
        int maxRetries,
        int retryDelaySeconds,
        int timeoutSeconds,
        Func<CancellationToken, Task<TResult>> runAttempt,
        Func<TResult, bool> isSuccess,
        Func<TResult> onTimeoutResult,
        Func<int, Task>? onAttemptTimedOutAsync,
        Func<RetryAttempt, Task>? onRetryAsync,
        Func<int, Task>? onLateSuccessAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runAttempt);
        ArgumentNullException.ThrowIfNull(isSuccess);
        ArgumentNullException.ThrowIfNull(onTimeoutResult);

        var maxAttempts = Math.Max(0, maxRetries);
        var delaySeconds = Math.Max(0, retryDelaySeconds);
        var attempt = 0;
        while (true)
        {
            var (result, timedOut) = await RunAttemptWithTimeoutAsync(
                runAttempt, timeoutSeconds, onTimeoutResult, ct).ConfigureAwait(false);

            if (timedOut && onAttemptTimedOutAsync is not null)
            {
                await onAttemptTimedOutAsync(timeoutSeconds).ConfigureAwait(false);
            }

            if (isSuccess(result))
            {
                if (attempt > 0 && onLateSuccessAsync is not null)
                {
                    await onLateSuccessAsync(attempt + 1).ConfigureAwait(false);
                }
                return new Outcome<TResult>(result, TimedOut: false, AttemptCount: attempt + 1);
            }

            if (attempt >= maxAttempts)
            {
                // Final attempt failed — caller applies its Required gate and,
                // for the server, surfaces the timeout from TimedOut.
                return new Outcome<TResult>(result, TimedOut: timedOut, AttemptCount: attempt + 1);
            }

            attempt++;
            if (onRetryAsync is not null)
            {
                var marker = FormatRetryMarker(stepName, attempt, maxAttempts, delaySeconds);
                await onRetryAsync(new RetryAttempt(
                    attempt, maxAttempts, delaySeconds, marker)).ConfigureAwait(false);
            }

            if (delaySeconds > 0)
            {
                // A deployment-level cancel during the delay propagates the OCE
                // out of the loop — same as the pre-extraction behaviour.
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs one attempt, wrapping it in a per-attempt timeout when
    /// <paramref name="timeoutSeconds"/> &gt; 0. Returns
    /// <c>(result, timedOut)</c>; a timeout caused by the per-attempt budget
    /// yields <c>(onTimeoutResult(), true)</c>, while a cancel from the outer
    /// token propagates.
    /// </summary>
    private static async Task<(TResult Result, bool TimedOut)> RunAttemptWithTimeoutAsync<TResult>(
        Func<CancellationToken, Task<TResult>> runAttempt,
        int timeoutSeconds,
        Func<TResult> onTimeoutResult,
        CancellationToken ct)
    {
        if (timeoutSeconds <= 0)
        {
            return (await runAttempt(ct).ConfigureAwait(false), false);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return (await runAttempt(linked.Token).ConfigureAwait(false), false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && linked.IsCancellationRequested)
        {
            // Per-step timeout (not a deployment-level cancel) — a failed attempt.
            return (onTimeoutResult(), true);
        }
    }

    /// <summary>
    /// The retry-marker log line. Byte-identical to the wording both the
    /// server orchestrator and the agent emitted before this was extracted —
    /// changing it would diff every retry log + audit detail.
    /// </summary>
    private static string FormatRetryMarker(
        string stepName, int attempt, int maxAttempts, int delaySeconds)
    {
        var inN = delaySeconds > 0
            ? $" in {delaySeconds.ToString(CultureInfo.InvariantCulture)}s"
            : string.Empty;
        return $"--- Step '{stepName}' attempt " +
               $"{attempt.ToString(CultureInfo.InvariantCulture)} failed; " +
               $"retrying{inN} (attempt {(attempt + 1).ToString(CultureInfo.InvariantCulture)} of " +
               $"{(maxAttempts + 1).ToString(CultureInfo.InvariantCulture)}) ---";
    }
}
