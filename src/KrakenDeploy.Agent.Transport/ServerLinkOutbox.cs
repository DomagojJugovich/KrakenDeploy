using System.Threading.Channels;
using KrakenDeploy.Contracts.Adhoc;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Transport;

/// <summary>One buffered agent→server report message. See <see cref="ServerLinkOutbox"/>.</summary>
public abstract record OutboxItem
{
    public sealed record Log(
        Guid DeploymentId, Guid DispatchId, int StepIndex, string Level, string Message) : OutboxItem;

    public sealed record StepCompleted(
        Guid DeploymentId,
        Guid DispatchId,
        int StepIndex,
        string StepName,
        bool Success,
        string? ErrorMessage,
        Dictionary<string, string> OutputVariables,
        List<string> SensitiveOutputNames) : OutboxItem;

    public sealed record DeploymentCompleted(
        Guid DeploymentId, Guid DispatchId, bool Success, string? ErrorMessage) : OutboxItem;

    public sealed record AdhocResult(AdhocScriptResult Result) : OutboxItem;
}

/// <summary>
/// B2 — at-least-once, strictly FIFO delivery pump for the agent's work-result
/// messages (step logs, step completions, deployment completions, adhoc
/// results). Callers enqueue and return immediately; a single pump task sends
/// items in order over the live hub connection and RETRIES an item until the
/// server acknowledges it — so a disconnect mid-deployment buffers reports
/// instead of losing them, and they flush when the connection returns.
/// <para>
/// Delivery semantics: at-least-once. A send that faults after reaching the
/// server (ack lost in a disconnect) is re-sent — the server dedups via the
/// DispatchId idempotency key (completions/step reports) or tolerates the
/// duplicate (log lines, adhoc TryResolve). Global FIFO through ONE pump plus
/// the server's sequential per-connection dispatch preserves the causal order
/// the orchestrator needs: a wave's step reports are acknowledged before its
/// completion goes out.
/// </para>
/// <para>
/// Bounds: completions and adhoc results are naturally bounded by plan size
/// and are never dropped. Log lines are capped at <see cref="LogCapacity"/>
/// queued items; beyond that the NEWEST line is dropped (counted + warned
/// locally — the agent's own rolling log file always retains everything).
/// The buffer is process-lifetime by design: agent death mid-deployment is
/// B1's lease/reconciler story, not the outbox's.
/// </para>
/// </summary>
public sealed class ServerLinkOutbox(
    Func<OutboxItem, CancellationToken, Task> sender,
    Func<bool> isConnected,
    ILogger logger)
{
    /// <summary>Max queued log lines (~1 MB at typical line sizes).</summary>
    internal const int LogCapacity = 5_000;

    /// <summary>How often the pump re-checks the connection while disconnected.</summary>
    internal static readonly TimeSpan DisconnectedPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Consecutive CONNECTED-send failures after which an item is dropped as
    /// poison (a hub-side rejection would otherwise wedge the queue forever).
    /// Failures while disconnected never count — those wait, not retry.
    /// </summary>
    internal const int MaxSendAttemptsPerItem = 5;

    private readonly Channel<OutboxItem> _queue = Channel.CreateUnbounded<OutboxItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private int _queuedLogCount;
    private long _droppedLogCount;

    /// <summary>Total log lines dropped over the log cap (test/diagnostics).</summary>
    public long DroppedLogCount => Interlocked.Read(ref _droppedLogCount);

    /// <summary>
    /// Queue a report for delivery. Never blocks. Returns <c>false</c> only
    /// when a log line was dropped over the cap (all other kinds always queue).
    /// </summary>
    public bool Enqueue(OutboxItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item is OutboxItem.Log)
        {
            if (Interlocked.Increment(ref _queuedLogCount) > LogCapacity)
            {
                Interlocked.Decrement(ref _queuedLogCount);
                var dropped = Interlocked.Increment(ref _droppedLogCount);
                // Warn on the first drop of a streak and then sparsely — the
                // full stream is still in the agent's local rolling log file.
                if (dropped == 1 || dropped % 1_000 == 0)
                {
                    logger.LogWarning(
                        "Outbox log buffer full ({Capacity} lines queued); dropped {Dropped} " +
                        "log line(s) so far while disconnected. Deployment verdicts are not " +
                        "affected; the agent's local log file retains everything.",
                        LogCapacity, dropped);
                }
                return false;
            }
        }

        // Unbounded channel: TryWrite only fails after writer completion,
        // which only happens at pump shutdown (process exit).
        return _queue.Writer.TryWrite(item);
    }

    /// <summary>
    /// The single drain loop. Runs for the life of the process (started once by
    /// <see cref="SignalRServerLink"/>); exits only when <paramref name="ct"/>
    /// fires. Sends strictly in order: the head item is retried until it is
    /// acknowledged, dropped as poison, or shutdown is requested.
    /// </summary>
    public async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (item is OutboxItem.Log)
                {
                    Interlocked.Decrement(ref _queuedLogCount);
                }

                await SendWithRetryAsync(item, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — anything still queued is intentionally lost (the
            // dispatch reconciler owns interrupted work server-side).
        }
    }

    private async Task SendWithRetryAsync(OutboxItem item, CancellationToken ct)
    {
        var connectedAttempts = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!isConnected())
            {
                // Waiting for the automatic reconnect / supervisor to bring the
                // connection back — this is not a send attempt. Seeing a real
                // disconnected period also proves earlier failures were the
                // transport, not a poison item: reset the poison counter.
                connectedAttempts = 0;
                await Task.Delay(DisconnectedPollInterval, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await sender(item, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Transient by default: connection dropped before/during the
                // invocation (the connection may already have flipped to
                // reconnecting). Retry the SAME item — order is preserved, and
                // the DispatchId key makes a might-have-arrived duplicate safe.
                // Hub-side errors (HubException) take the same capped path: a
                // transient server fault (e.g. DB blip inside the hub method)
                // gets retried; a deterministic rejection drops after the cap.
                connectedAttempts++;
                if (connectedAttempts >= MaxSendAttemptsPerItem)
                {
                    logger.LogError(ex,
                        "Outbox item {Item} failed {Attempts} consecutive sends while " +
                        "connected; dropped as poison to keep the queue moving.",
                        item.GetType().Name, connectedAttempts);
                    return;
                }

                logger.LogDebug(ex,
                    "Outbox send of {Item} failed (attempt {Attempt}); will retry.",
                    item.GetType().Name, connectedAttempts);

                // Brief pause so a flapping connection doesn't spin the loop.
                await Task.Delay(DisconnectedPollInterval, ct).ConfigureAwait(false);
            }
        }

        ct.ThrowIfCancellationRequested();
    }
}
