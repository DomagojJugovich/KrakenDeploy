namespace KrakenDeploy.Contracts.Adhoc;

/// <summary>
/// M11.E.7 — what the agent sends back to the server after running (or
/// refusing to run) an <see cref="AdhocScriptCommand"/>. The server resolves
/// the source target id from the connection's <c>NameIdentifier</c> claim
/// (same pattern as deployment hub methods), so the result intentionally
/// does not carry a <c>TargetId</c> over the wire.
/// <para>
/// <strong>Outcome semantics:</strong>
/// <list type="bullet">
///   <item><see cref="AgentError"/> non-null → the script never ran (signature
///         mismatch, missing public key, process-start failure, …). In this
///         case <see cref="ExitCode"/>, <see cref="Stdout"/>, <see cref="Stderr"/>
///         carry diagnostic info only.</item>
///   <item><see cref="AgentError"/> null → the script ran; <see cref="ExitCode"/>
///         is authoritative (0 = success).</item>
/// </list>
/// </para>
/// </summary>
public sealed record AdhocScriptResult(
    /// <summary>The session the result belongs to. Matches
    /// <see cref="AdhocScriptCommand.SessionId"/>.</summary>
    Guid SessionId,
    /// <summary>The iteration the result belongs to. Matches
    /// <see cref="AdhocScriptCommand.IterNumber"/>.</summary>
    int IterNumber,
    /// <summary>The process exit code, or <c>-1</c> when the script never
    /// ran (see <see cref="AgentError"/>).</summary>
    int ExitCode,
    /// <summary>Captured stdout (info-level lines).</summary>
    string Stdout,
    /// <summary>Captured stderr (error-level lines).</summary>
    string Stderr,
    /// <summary>Non-null when the agent refused to run or hit a transport-level
    /// failure (signature mismatch, no public key, runtime exception). Null on
    /// every path where the script actually executed.</summary>
    string? AgentError)
{
    /// <summary>True when the script ran and exited with code 0.</summary>
    public bool Success => AgentError is null && ExitCode == 0;
}
