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
    /// F5 CONTRACT CHANGE (v2 → v3; was F2) — which SIDE of the agent's
    /// reader-writer machine execution gate this script takes. <c>false</c> (the
    /// default) → EXCLUSIVE: the script excludes, and is excluded by, every other
    /// unit of work on that box. <c>true</c> → SHARED: it co-runs with other shared
    /// work but still queues behind an exclusive holder.
    /// <para>
    /// It is NOT a bypass. Under F2 <c>true</c> meant "skip the gate entirely", and a
    /// v2 agent still reads it that way — which is why the wire contract had to bump
    /// even though the shape did not change: the skew is invisible on the wire and
    /// must be refused at registration.
    /// </para>
    /// <para>
    /// Who sets it: the AI ad-hoc session flow always sends <c>true</c> (locked
    /// decision P5 — an LLM-generated, gate-checked, operator-approved script is
    /// read-always and never excludes). WP16's script console maps its per-run "allow
    /// running concurrently with other scripts" checkbox onto it, unchecked (the
    /// default) → <c>false</c> → EXCLUSIVE, because a hand-written script has no mode
    /// gate. It is therefore per-RUN, not per-target.
    /// </para>
    /// <para>
    /// Deliberately OUTSIDE the signature binding
    /// (<see cref="AdhocScriptSigner"/> binds <c>(SessionId, IterNumber,
    /// Script)</c>): it is a local execution-serialization hint, not an
    /// authorization input — flipping it cannot make a script run that the
    /// operator did not approve, only change what it may co-run with. The blast radius
    /// is also strictly smaller than under F2: the worst a flip to <c>true</c> can buy
    /// is the SHARED side, which still queues behind any exclusive holder, whereas F2's
    /// <c>true</c> meant no lock at all. There is no configuration knob behind this —
    /// the dispatching flow decides it per run.
    /// </para>
    /// </summary>
    bool AllowParallelTaskExecution = false);
