namespace KrakenDeploy.Server.Core.Domain.Targets;

public enum TransportMode
{
    /// <summary>SignalR — the agent opens a persistent outbound connection to the
    /// server; the server pushes work back down the same full-duplex connection.
    /// The only live-agent transport.</summary>
    Reverse = 0,

    // Values 1 (Direct) and 2 (Polling) were retired — KrakenDeploy is SignalR-only
    // for live agents. The numbering gap is intentional so persisted OfflineDrop
    // rows (= 3) keep their value and no migration is needed.

    /// <summary>No live agent — the server emits a drop bundle that is executed
    /// manually on an air-gapped target, then the result is uploaded back.</summary>
    OfflineDrop = 3,
}
