namespace KrakenDeploy.Server.Core.Domain.Deployments;

/// <summary>
/// Why/how a <see cref="ServerTask"/> was created — its provenance (schema-hardening
/// fix 6). Stored as an int so adding a variant is additive.
/// <para>
/// The enum is deliberately <b>1-based</b>: <see cref="Unspecified"/> = 0 is the
/// invalid/unset sentinel. A non-nullable C# enum property defaults to 0, so a DB
/// <c>NOT NULL</c> constraint alone can't tell a forgotten cause from a real one —
/// but a 0-is-invalid sentinel lets the creation guard
/// (<see cref="TaskInitiator.EnsureValid"/>) reject <c>default</c> and guarantee
/// every persisted task carries a real cause.
/// </para>
/// </summary>
public enum ServerTaskCause
{
    /// <summary>Unset — never persisted. This is <c>default(ServerTaskCause)</c>;
    /// the creation guard rejects it.</summary>
    Unspecified = 0,

    /// <summary>A human clicked Deploy/Run in the web UI.</summary>
    Manual = 1,

    /// <summary>REST API call (Web API), authenticated by session cookie or API key.</summary>
    Api = 2,

    /// <summary>The KrakenDeploy CLI (distinguished from a generic REST caller by
    /// the <c>X-Kraken-Client: cli</c> header its HTTP client sends).</summary>
    Cli = 3,

    /// <summary>An MCP tool invocation.</summary>
    Mcp = 4,

    /// <summary>A scheduled task that reached its start time.</summary>
    Scheduled = 5,

    /// <summary>A child task created by an <c>Octopus.DeployRelease</c> step running
    /// inside a parent task.</summary>
    ParentStep = 6,

    /// <summary>Reconstructed from an offline-drop result import (air-gapped path).
    /// Reserved: no online creation site emits this today — offline-drop tasks are
    /// created via the normal online path before the bundle is built, and the import
    /// only transitions the existing task to terminal.</summary>
    OfflineImport = 7,

    /// <summary>An event subscription fired and triggered a runbook run
    /// (<c>RunbookTransport</c>). Automated; no acting user.</summary>
    Subscription = 8,

    // 9 is reserved for Trigger (scheduled/automatic deployment triggers) — not
    // yet emitted; do not reuse this slot for anything else.
}
