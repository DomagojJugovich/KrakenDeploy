namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// Provenance for a newly-created <see cref="ServerTask"/>: who initiated it and
/// why (schema-hardening fix 6). Passed explicitly into every task-creation API —
/// there is no ambient current-user capture on this codebase, so each creation site
/// supplies it.
/// <para>
/// Construct via the static factories (<see cref="Manual"/>, <see cref="Api"/>, …)
/// so <see cref="Cause"/> and <see cref="Display"/> are always valid. Because it is
/// a non-nullable value type with no public constructor, a creation method that
/// takes a <see cref="TaskInitiator"/> parameter forces every caller to supply one
/// at compile time; <see cref="EnsureValid"/> is the run-time service-layer guard
/// that additionally rejects a <c>default</c> (unset) value.
/// </para>
/// </summary>
public readonly record struct TaskInitiator
{
    /// <summary>Column cap shared with <c>ServerTaskConfiguration</c> for
    /// <c>created_by_display</c>.</summary>
    public const int MaxDisplayLength = 256;

    /// <summary>Column cap shared with <c>ServerTaskConfiguration</c> for
    /// <c>cause_detail</c>.</summary>
    public const int MaxDetailLength = 256;

    private TaskInitiator(ServerTaskCause cause, string display, Guid? userId, string? detail)
    {
        Cause = cause;
        Display = display;
        UserId = userId;
        Detail = detail;
    }

    /// <summary>Why/how the task was created. Never <see cref="ServerTaskCause.Unspecified"/>
    /// once built through a factory.</summary>
    public ServerTaskCause Cause { get; }

    /// <summary>Denormalized human label of the initiator (the acting user's name, or
    /// a <c>"System (…)"</c> label for automated causes). Survives user deletion and
    /// covers causes with no user. Never null/empty.</summary>
    public string Display { get; }

    /// <summary>Acting user id when a human is attributable; <c>null</c> for automated
    /// causes. Stored as a FK to <c>users</c> that is <c>SET NULL</c> on user delete —
    /// the denormalized <see cref="Display"/> keeps the provenance readable afterwards.</summary>
    public Guid? UserId { get; }

    /// <summary>Optional extra provenance — parent task id, API-key name, subscription
    /// + event ids, etc. Truncated to <see cref="MaxDetailLength"/>.</summary>
    public string? Detail { get; }

    // ── Factories — each guarantees a valid Cause + non-empty Display ────────

    /// <summary>A human acting in the web UI.</summary>
    public static TaskInitiator Manual(Guid? userId, string? display, string? detail = null)
        => Create(ServerTaskCause.Manual, display, userId, detail);

    /// <summary>A REST API caller (session or API-key authenticated).</summary>
    public static TaskInitiator Api(Guid? userId, string? display, string? detail = null)
        => Create(ServerTaskCause.Api, display, userId, detail);

    /// <summary>The KrakenDeploy CLI (the API-key owner is the acting user).</summary>
    public static TaskInitiator Cli(Guid? userId, string? display, string? detail = null)
        => Create(ServerTaskCause.Cli, display, userId, detail);

    /// <summary>An MCP tool invocation (the API-key owner is the acting user).</summary>
    public static TaskInitiator Mcp(Guid? userId, string? display, string? detail = null)
        => Create(ServerTaskCause.Mcp, display, userId, detail);

    /// <summary>A scheduled task reaching its start time (no acting user).</summary>
    public static TaskInitiator Scheduled(string? detail = null)
        => Create(ServerTaskCause.Scheduled, null, null, detail);

    /// <summary>A child task spawned by an <c>Octopus.DeployRelease</c> step. Inherits
    /// the parent task's acting user + display so the child attributes to the human who
    /// launched the parent; <see cref="Detail"/> records the parent task id.</summary>
    public static TaskInitiator ParentStep(Guid? inheritedUserId, string? inheritedDisplay, Guid parentTaskId)
        => Create(ServerTaskCause.ParentStep, inheritedDisplay, inheritedUserId, $"parent:{parentTaskId}");

    /// <summary>Reconstructed from an offline-drop import.</summary>
    public static TaskInitiator OfflineImport(Guid? userId, string? display, string? detail = null)
        => Create(ServerTaskCause.OfflineImport, display, userId, detail);

    /// <summary>An event subscription that triggered a runbook run (no acting user).</summary>
    public static TaskInitiator Subscription(string? detail = null)
        => Create(ServerTaskCause.Subscription, null, null, detail);

    private static TaskInitiator Create(ServerTaskCause cause, string? display, Guid? userId, string? detail)
    {
        var d = string.IsNullOrWhiteSpace(display) ? DefaultDisplay(cause) : display.Trim();
        if (d.Length > MaxDisplayLength)
        {
            d = d[..MaxDisplayLength];
        }

        var det = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
        if (det is { Length: > MaxDetailLength })
        {
            det = det[..MaxDetailLength];
        }

        return new TaskInitiator(cause, d, userId, det);
    }

    /// <summary>Fallback display when the caller could not resolve a human name (e.g.
    /// an authenticated principal with no Name/Email claim, or an automated cause).</summary>
    private static string DefaultDisplay(ServerTaskCause cause) => cause switch
    {
        ServerTaskCause.Manual => "Unknown",
        ServerTaskCause.Api => "API",
        ServerTaskCause.Cli => "CLI",
        ServerTaskCause.Mcp => "mcp-client",
        ServerTaskCause.Scheduled => "System (scheduled)",
        ServerTaskCause.ParentStep => "System (deploy-release step)",
        ServerTaskCause.OfflineImport => "System (offline import)",
        ServerTaskCause.Subscription => "System (subscription)",
        _ => "System",
    };

    /// <summary>Service-layer guard: throws when this initiator is the <c>default</c>
    /// (unset) value or has no display. Called by every task-creation funnel via
    /// <see cref="StampOnto"/>.</summary>
    public void EnsureValid()
    {
        if (Cause == ServerTaskCause.Unspecified)
        {
            throw new InvalidOperationException(
                "ServerTask creation requires a cause (TaskInitiator was default/unset). " +
                "Build it with one of the TaskInitiator factory methods.");
        }

        if (string.IsNullOrWhiteSpace(Display))
        {
            throw new InvalidOperationException("TaskInitiator.Display must be non-empty.");
        }
    }

    /// <summary>Stamps this provenance onto a task after validating it. The single
    /// place the four provenance columns are written.</summary>
    public void StampOnto(ServerTask task)
    {
        EnsureValid();
        task.Cause = Cause;
        task.CreatedByDisplay = Display;
        task.CreatedByUserId = UserId;
        task.CauseDetail = Detail;
    }
}
