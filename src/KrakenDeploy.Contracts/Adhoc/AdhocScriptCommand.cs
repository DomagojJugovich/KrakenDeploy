namespace KrakenDeploy.Contracts.Adhoc;

/// <summary>
/// M11.E.7 — the payload sent server → agent over the SignalR control-plane
/// when the operator has approved an iteration's script. The same payload is
/// dispatched to every target in the session's frozen set
/// (<c>AdhocSession.FrozenTargetSetJson</c>); per-agent fan-out happens
/// server-side. The agent's <c>AdhocScriptExecutor</c>:
/// <list type="number">
///   <item>Loads its trusted public key from <c>Adhoc:TrustedPublicKey</c>.</item>
///   <item>Verifies the signature against
///         <c>(SessionId, IterNumber, Script)</c> via
///         <see cref="KrakenDeploy.Contracts.Adhoc.AdhocScriptSigner.Verify"/>.</item>
///   <item>If valid, runs the script via the existing <c>ScriptRunner</c>;
///         if not, refuses to execute and reports an
///         <see cref="AdhocScriptResult.AgentError"/> back.</item>
/// </list>
/// <para>
/// The agent never trusts the script payload — only the signature gate
/// decides whether it runs. A tampered <see cref="Script"/> changes the
/// canonical bytes <see cref="AdhocScriptSigner.Verify"/> hashes, so any
/// in-flight modification (man-in-the-middle, mis-routed payload, …) fails
/// verification and the script never executes.
/// </para>
/// </summary>
public sealed record AdhocScriptCommand(
    /// <summary>Owning <c>AdhocSession.Id</c>. Bound into the signature.</summary>
    Guid SessionId,
    /// <summary>1-based iteration number within the session. Bound into the
    /// signature so a signed payload from iteration <c>N</c> cannot be
    /// replayed as iteration <c>N+1</c>.</summary>
    int IterNumber,
    /// <summary>The exact PowerShell text the operator approved. Bytes
    /// verbatim — any whitespace difference breaks verification.</summary>
    string Script,
    /// <summary>Base64 RSA-SHA256 signature produced server-side via
    /// <see cref="AdhocScriptSigner.Sign"/> using the
    /// <c>Adhoc:SigningKey</c>.</summary>
    string Signature,
    /// <summary>
    /// F2 CONTRACT CHANGE — the receiving target's
    /// <c>DeploymentTarget.AllowParallelTaskExecution</c>. Stamped per target by
    /// the dispatcher (the same command text fans out to the frozen set, but this
    /// flag is per-machine). <c>false</c> (the default) makes the script take the
    /// agent's machine execution gate, so it waits its turn behind a running
    /// deployment / runbook run instead of interleaving with it; <c>true</c>
    /// bypasses the gate.
    /// <para>
    /// Deliberately OUTSIDE the signature binding
    /// (<see cref="AdhocScriptSigner"/> binds <c>(SessionId, IterNumber,
    /// Script)</c>): it is a local execution-serialization hint, not an
    /// authorization input — flipping it cannot make a script run that the
    /// operator did not approve, only change whether it interleaves. Server
    /// configuration, not agent state, is the source of truth.
    /// </para>
    /// </summary>
    bool AllowParallelTaskExecution = false);
