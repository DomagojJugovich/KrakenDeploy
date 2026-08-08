namespace KrakenDeploy.Agent.Services;

/// <summary>
/// Agent auto-update configuration. Bound to the "Agent:Update" configuration section.
/// </summary>
public sealed class AgentUpdateConfig
{
    /// <summary>When false, this agent will never check for or apply updates.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often to check for updates. Default 5 minutes.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maintenance window start (local time). Default 02:00.</summary>
    public TimeOnly MaintenanceWindowStart { get; set; } = new(2, 0);

    /// <summary>Maintenance window end (local time). Default 04:00.</summary>
    public TimeOnly MaintenanceWindowEnd { get; set; } = new(4, 0);

    /// <summary>
    /// C6 — how long the newly-installed version has to register healthy after a
    /// self-upgrade restart before the agent automatically rolls back to the
    /// backed-up previous version. This window is granted afresh on every restart
    /// (a delayed restart / machine reboot must not consume it), so it must
    /// comfortably exceed the agent's normal cold-start + registration time.
    /// Default 3 minutes.
    /// </summary>
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// C6 — how many times the new version may restart WITHOUT ever confirming
    /// health before the agent gives up and rolls back. Bounds a crash-loop where
    /// the new binary dies before its per-restart <see cref="HealthCheckTimeout"/>
    /// window elapses (each restart alone would otherwise get a fresh window and
    /// never trip the timeout). Default 3.
    /// </summary>
    public int MaxHealthAttempts { get; set; } = 3;

    /// <summary>
    /// F5 (locked decision P8) — how long the swap may wait for the machine
    /// execution gate's EXCLUSIVE side before giving up and retrying next tick.
    /// <para>
    /// The gate is writer-fair, so queueing here also BLOCKS new work from starting.
    /// That is the point — the swap must not begin while anything is running, and the
    /// old <c>IsExecuting</c> pre-check was both blind to ad-hoc work and a TOCTOU —
    /// but it is why the wait must be BOUNDED: an unbounded one would let a wedged
    /// holder stop the agent from accepting work for the rest of the process's life.
    /// On expiry nothing is swapped, the queued writer leaves, work resumes, and the
    /// next tick tries again.
    /// </para>
    /// <para>
    /// Default 2 minutes, and it must stay comfortably below TWO other durations —
    /// <see cref="AgentUpdateConfigValidator"/> enforces the first and documents the
    /// second:
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="CheckInterval"/>. At equal values (the shipped 5/5 pair, now
    ///     corrected) <see cref="PeriodicTimer"/> has a coalesced tick waiting the
    ///     instant the wait expires, so the updater re-queues a machine-blocking writer
    ///     back-to-back and blocks work for essentially the whole maintenance window
    ///     without ever completing a swap.</item>
    ///   <item><c>Adhoc:MaxTotalDuration</c> (also 5 min). An ad-hoc script's budget
    ///     spans its queue wait, so a swap window as long as that budget guarantees any
    ///     script arriving behind the queued writer is refused on its own deadline —
    ///     and refused with a message blaming a holder that never existed.</item>
    /// </list>
    /// </summary>
    public TimeSpan SwapGateTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
