using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KrakenDeploy.Agent.Deployment;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts.Adhoc;
using KrakenDeploy.Steps.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Adhoc;

/// <summary>
/// M11.E.7 — agent-side handler for <see cref="AdhocScriptCommand"/>. Hooked
/// into <see cref="IServerLink.OnRunAdhocScript"/> at agent startup. Fail-
/// closed by construction:
/// <list type="number">
///   <item>Loads the trusted public key from <c>Adhoc:TrustedPublicKey</c>
///         (inline PEM or path to a .pem file). Missing key → REFUSE.</item>
///   <item>Verifies <see cref="AdhocScriptCommand.Signature"/> against the
///         command's <c>(SessionId, IterNumber, Script)</c> binding via
///         <see cref="AdhocScriptSigner.Verify"/>. Mismatch → REFUSE.</item>
///   <item>F2 — takes the machine execution gate (unless the target sets
///         <see cref="AdhocScriptCommand.AllowParallelTaskExecution"/>) so the
///         script waits its turn behind a running deployment / runbook run
///         instead of interleaving with its file / IIS / service operations.
///         Bounded by <see cref="GateWaitTimeout"/> → REFUSE on expiry.</item>
///   <item>If verification passes, runs the script via <see cref="ScriptRunner"/>
///         capturing stdout / stderr / exit code into a structured
///         <see cref="AdhocScriptResult"/>.</item>
///   <item>Reports the result back via
///         <see cref="IServerLink.ReportAdhocResultAsync"/> — ALWAYS, including
///         on every refusal path; the server's TCS slot is waiting and a
///         silent agent would deadlock the dispatcher.</item>
/// </list>
/// <para>
/// The executor never throws past its public surface (modulo a CT-cancelled
/// shutdown); a runtime exception during script execution is captured into the
/// reported <see cref="AdhocScriptResult.AgentError"/> so the dispatcher
/// always gets a closing message.
/// </para>
/// </summary>
public sealed class AdhocScriptExecutor(
    IServerLink serverLink,
    IConfiguration config,
    IAdhocScriptInvoker invoker,
    MachineExecutionGate executionGate,
    ILogger<AdhocScriptExecutor> logger)
{
    /// <summary>
    /// Working directory the script runs in. Created lazily under the agent's
    /// staging tree; cleaned up after the run.
    /// </summary>
    private static readonly string StagingRoot =
        Path.Combine(Path.GetTempPath(), "kraken-adhoc");

    /// <summary>
    /// F2 default for the bounded gate wait: how long a queued ad-hoc script waits
    /// for the machine execution slot before REFUSING to run.
    /// <para>
    /// Deliberately shorter than <c>AdhocDispatcher.DefaultTimeout</c> (5 min, the
    /// server's per-target wait) so a script the server has already given up on
    /// never executes late — an operator who saw "timed out" and approved a fresh
    /// iteration must not get both. Operators who raise the dispatcher timeout
    /// should raise <c>Adhoc:MaxQueueWait</c> to match; the two ends are coupled
    /// by intent, not by the wire.
    /// </para>
    /// </summary>
    private static readonly TimeSpan DefaultGateWaitTimeout = TimeSpan.FromMinutes(4);

    /// <summary>
    /// Test-only override for the bounded gate wait. <c>null</c> (production) reads
    /// <c>Adhoc:MaxQueueWait</c> instead. Nullable rather than a <c>TimeSpan.Zero</c>
    /// sentinel so a test CAN ask for "refuse immediately"; note the DI container
    /// does property injection for nobody, so production always sees <c>null</c>.
    /// </summary>
    internal TimeSpan? GateWaitTimeout { get; init; }

    /// <summary>
    /// Resolves the bounded gate wait: the test override, else
    /// <c>Adhoc:MaxQueueWait</c> (a <see cref="TimeSpan"/> string, e.g.
    /// <c>00:04:00</c>), else <see cref="DefaultGateWaitTimeout"/>. A non-positive
    /// configured value falls back to the default rather than degenerating into
    /// "refuse immediately". A METHOD, not a property: it reads configuration, so
    /// the call site should show that it is not free — read it once into a local.
    /// </summary>
    private TimeSpan ResolveGateWaitTimeout()
    {
        if (GateWaitTimeout is { } explicitWait)
        {
            return explicitWait;
        }
        var configured = config["Adhoc:MaxQueueWait"];
        return TimeSpan.TryParse(configured, CultureInfo.InvariantCulture, out var parsed)
               && parsed > TimeSpan.Zero
            ? parsed
            : DefaultGateWaitTimeout;
    }

    /// <summary>Entry point — wire this to <c>IServerLink.OnRunAdhocScript</c>.
    /// <paramref name="hostStopping"/> (F2-followup 1) is the agent host's shutdown
    /// token, observed while QUEUED on the machine gate so a script waiting its turn
    /// unwinds at shutdown instead of parking on a disposed semaphore. It is
    /// deliberately NOT passed to the invoker: a shutdown must not half-kill a script
    /// that is already running (and the server has no ad-hoc abort to reconcile
    /// with).</summary>
    public async Task HandleAsync(
        AdhocScriptCommand command, CancellationToken hostStopping = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Step 1: load the trusted key. No key configured = refuse + report.
        RSA? publicKey;
        try
        {
            publicKey = LoadTrustedPublicKey();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "AdhocScriptExecutor: failed to load Adhoc:TrustedPublicKey for " +
                "session {SessionId} iter {Iter}; refusing.",
                command.SessionId, command.IterNumber);
            await ReportFailureAsync(command,
                $"Failed to load Adhoc:TrustedPublicKey: {ex.Message}").ConfigureAwait(false);
            return;
        }

        if (publicKey is null)
        {
            logger.LogError(
                "AdhocScriptExecutor: refusing session {SessionId} iter {Iter} — " +
                "Adhoc:TrustedPublicKey is not configured on this agent.",
                command.SessionId, command.IterNumber);
            await ReportFailureAsync(command,
                "Agent has no Adhoc:TrustedPublicKey configured; refusing to run.")
                .ConfigureAwait(false);
            return;
        }

        // Step 2: verify the signature. Fail closed on any mismatch.
        AdhocScriptSigner.VerifyResult verify;
        using (publicKey)
        {
            verify = AdhocScriptSigner.Verify(
                command.SessionId, command.IterNumber,
                command.Script, command.Signature, publicKey);
        }

        if (!verify.IsValid)
        {
            logger.LogError(
                "AdhocScriptExecutor: signature mismatch for session {SessionId} " +
                "iter {Iter} — {Reason}",
                command.SessionId, command.IterNumber, verify.Reason);
            await ReportFailureAsync(command,
                $"Signature verification failed: {verify.Reason}").ConfigureAwait(false);
            return;
        }

        // Step 3 (F2): take the machine execution slot unless the target allows
        // parallel task execution. Pre-F2 ad-hoc scripts bypassed the gate outright,
        // so a diagnostic script could run straight into a deployment's file / IIS /
        // service operations on the same box. Bounded so a long-running deployment
        // cannot pin an interactive script indefinitely.
        var slot = await AcquireMachineSlotAsync(command, hostStopping).ConfigureAwait(false);
        if (slot.Refused)
        {
            return; // AcquireMachineSlotAsync already reported the refusal.
        }

        // Disposing the lease hands the machine to the next queued task; a
        // bypassing script holds none, and disposing null is a no-op.
        using (slot.Lease)
        {
            await RunAndReportAsync(command).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One acquisition attempt's outcome. <see cref="Refused"/> means the script was
    /// NOT executed and the failure has ALREADY been reported to the dispatcher, so
    /// the caller must return silently.
    /// </summary>
    private readonly record struct AdhocMachineSlot(
        MachineExecutionGate.Releaser? Lease, bool Refused);

    /// <summary>
    /// F2 — takes the machine execution slot for an ad-hoc script, bounded by
    /// <see cref="ResolveGateWaitTimeout"/>. Refuses (and reports) rather than
    /// running late when the slot stays held: a script the server's dispatcher has
    /// already resolved as timed out must not execute afterwards. Bypasses the gate
    /// entirely when the target sets
    /// <see cref="AdhocScriptCommand.AllowParallelTaskExecution"/>.
    /// </summary>
    private async Task<AdhocMachineSlot> AcquireMachineSlotAsync(
        AdhocScriptCommand command, CancellationToken hostStopping)
    {
        if (command.AllowParallelTaskExecution)
        {
            logger.LogInformation(
                "Adhoc session {SessionId} iter {Iter} bypasses the machine execution gate: " +
                "the target allows parallel task execution.",
                command.SessionId, command.IterNumber);
            return new AdhocMachineSlot(null, false);
        }

        var gateWait = ResolveGateWaitTimeout();
        try
        {
            if (await executionGate.TryAcquireNowAsync(hostStopping)
                    .ConfigureAwait(false) is { } uncontended)
            {
                return new AdhocMachineSlot(uncontended, false);
            }

            logger.LogInformation(
                "Adhoc session {SessionId} iter {Iter} is waiting up to {Timeout} for the " +
                "machine execution slot (another task is running on this machine).",
                command.SessionId, command.IterNumber, gateWait);

            if (await executionGate.AcquireAsync(gateWait, hostStopping)
                    .ConfigureAwait(false) is { } queued)
            {
                return new AdhocMachineSlot(queued, false);
            }

            logger.LogWarning(
                "Adhoc session {SessionId} iter {Iter} refused: the machine execution " +
                "slot was still held after {Timeout}.",
                command.SessionId, command.IterNumber, gateWait);
            await ReportFailureAsync(command,
                $"Another task is still running on this machine after {gateWait}; " +
                "the script was NOT executed. Re-run it once the machine is free.")
                .ConfigureAwait(false);
            return new AdhocMachineSlot(null, true);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            // The agent host is shutting down: either hostStopping fired while this
            // script was QUEUED on the gate (OperationCanceledException), or DI got
            // there first and disposed the gate (ObjectDisposedException). Report
            // rather than let either escape — the dispatcher's slot is waiting and
            // this class's contract is that every path closes it.
            logger.LogWarning(ex,
                "Adhoc session {SessionId} iter {Iter} refused: the agent is shutting down.",
                command.SessionId, command.IterNumber);
            await ReportFailureAsync(command,
                "The agent is shutting down; the script was NOT executed.")
                .ConfigureAwait(false);
            return new AdhocMachineSlot(null, true);
        }
    }

    /// <summary>
    /// Runs the verified script and reports its outcome. Split out of
    /// <see cref="HandleAsync"/> so the machine-slot lease wraps it in a plain
    /// <c>using</c> — keeping this body at its natural indentation instead of
    /// nesting 60-odd pre-existing lines inside a try/finally.
    /// </summary>
    private async Task RunAndReportAsync(AdhocScriptCommand command)
    {
        // Step 4: execute via ScriptRunner; capture stdout / stderr / exit code.
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var workingDir = Path.Combine(StagingRoot, command.SessionId.ToString("N"),
            command.IterNumber.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(workingDir);

        int exitCode;
        try
        {
            exitCode = await invoker.InvokeAsync(
                command.Script,
                workingDir,
                envVars: new Dictionary<string, string>
                {
                    ["KrakenAdhocSessionId"]  = command.SessionId.ToString(),
                    ["KrakenAdhocIterNumber"] = command.IterNumber.ToString(CultureInfo.InvariantCulture),
                },
                onOutput: (level, line) =>
                {
                    (level == "error" ? stderr : stdout).AppendLine(line);
                    return Task.CompletedTask;
                },
                ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "AdhocScriptExecutor: script execution threw for session {SessionId} iter {Iter}.",
                command.SessionId, command.IterNumber);
            await ReportFailureAsync(command,
                $"Script execution threw: {ex.Message}",
                stdout.ToString(), stderr.ToString()).ConfigureAwait(false);
            TryCleanWorkingDir(workingDir);
            return;
        }

        TryCleanWorkingDir(workingDir);

        // Step 5: report.
        var result = new AdhocScriptResult(
            SessionId:  command.SessionId,
            IterNumber: command.IterNumber,
            ExitCode:   exitCode,
            Stdout:     stdout.ToString(),
            Stderr:     stderr.ToString(),
            AgentError: null);

        try
        {
            await serverLink.ReportAdhocResultAsync(result, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort — the server's TCS will eventually time out if we
            // can't reach it. Log so the operator sees the agent ran but the
            // result didn't make it back.
            logger.LogError(ex,
                "AdhocScriptExecutor: failed to report result for session {SessionId} iter {Iter}.",
                command.SessionId, command.IterNumber);
        }
    }

    private async Task ReportFailureAsync(
        AdhocScriptCommand command, string reason,
        string stdout = "", string stderr = "")
    {
        var result = new AdhocScriptResult(
            SessionId:  command.SessionId,
            IterNumber: command.IterNumber,
            ExitCode:   -1,
            Stdout:     stdout,
            Stderr:     stderr,
            AgentError: reason);

        try
        {
            await serverLink.ReportAdhocResultAsync(result, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "AdhocScriptExecutor: also failed to report the failure for session " +
                "{SessionId} iter {Iter}.", command.SessionId, command.IterNumber);
        }
    }

    /// <summary>
    /// Reads the trusted public key from <c>Adhoc:TrustedPublicKey</c>.
    /// Accepts inline PEM (multi-line with BEGIN marker) or a path to a
    /// <c>.pem</c> file. Returns null when missing; throws when malformed
    /// (caller treats both as a refuse-and-report).
    /// </summary>
    internal RSA? LoadTrustedPublicKey()
    {
        var raw = config["Adhoc:TrustedPublicKey"];
        if (string.IsNullOrWhiteSpace(raw)) { return null; }

        string pem;
        if (raw.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            pem = raw;
        }
        else if (File.Exists(raw))
        {
            pem = File.ReadAllText(raw);
        }
        else
        {
            throw new InvalidOperationException(
                $"Adhoc:TrustedPublicKey is set but is neither inline PEM nor a path " +
                $"to an existing file: '{raw}'.");
        }

        return AdhocScriptSigner.ImportPublicKeyFromPem(pem);
    }

    private static void TryCleanWorkingDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* non-fatal */ }
    }
}

/// <summary>
/// Thin abstraction over the PowerShell process so the executor can be
/// unit-tested without requiring <c>pwsh</c> on the test machine. Production
/// code uses <see cref="ScriptRunnerInvoker"/>; tests supply a fake that
/// returns canned stdout/stderr/exitCode.
/// </summary>
public interface IAdhocScriptInvoker
{
    Task<int> InvokeAsync(
        string script,
        string workingDirectory,
        IReadOnlyDictionary<string, string> envVars,
        Func<string, string, Task> onOutput,
        CancellationToken ct);
}

/// <summary>
/// Production <see cref="IAdhocScriptInvoker"/> backed by
/// <see cref="ScriptRunner"/> with the syntax fixed to PowerShell — ad-hoc
/// actions are PowerShell-only by design (the LLM generation prompt + gate
/// are tuned for it).
/// </summary>
public sealed class ScriptRunnerInvoker : IAdhocScriptInvoker
{
    public Task<int> InvokeAsync(
        string script,
        string workingDirectory,
        IReadOnlyDictionary<string, string> envVars,
        Func<string, string, Task> onOutput,
        CancellationToken ct)
    {
        // C5/T1-20: ad-hoc runs PowerShell directly (no step-handler preamble), so
        // force UTF-8 output here too — otherwise Croatian (č ć š ž đ) in the
        // captured output is mangled by Windows PowerShell 5.1's OEM code page
        // (the runner already decodes stdout as UTF-8). Mirrors the step preamble.
        var utf8Script =
            "try { $OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }\r\n"
            + script;
        return new ScriptRunner().RunAndReturnExitCodeAsync(
            utf8Script, syntax: "PowerShell",
            workingDirectory, envVars, onOutput, ct, powerShellEdition: null);
    }
}
