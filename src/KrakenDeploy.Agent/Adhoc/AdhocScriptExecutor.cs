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
///         instead of interleaving with its file / IIS / service operations.</item>
///   <item>F2-followup 3 — ONE budget (<see cref="MaxTotalDuration"/>) from receipt
///         covers the queue wait AND the run, so the script can never still be
///         executing after the server's dispatcher has reported it timed out.
///         Expiry while queued → REFUSE; expiry while running → the process tree is
///         killed and the partial output reported.</item>
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
    /// F2-followup 3 default for <see cref="MaxTotalDuration"/>: the agent's whole
    /// budget for one ad-hoc command, measured from RECEIPT and split across queueing
    /// on the machine gate and running the script.
    /// <para>
    /// Matches <c>AdhocDispatcher.DefaultTimeout</c> — the server's per-target wait,
    /// also measured from dispatch — because that is the deadline the operator
    /// actually sees. F2 bounded only the queue WAIT, which left two ways to execute
    /// after the dispatcher had already reported "timed out": queue 3:59 then run for
    /// minutes, or hold the slot forever (the invoker got
    /// <see cref="CancellationToken.None"/> and <c>ScriptRunner</c> has no internal
    /// timeout, so one hung diagnostic blocked every later deployment on that box
    /// until the agent was restarted). One budget from receipt closes both.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan DefaultMaxTotalDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Upper bound accepted for <c>Adhoc:MaxTotalDuration</c>. Guards the
    /// <see cref="SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)"/> /
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> range and, more
    /// usefully, catches the bare-integer footgun: <c>TimeSpan.Parse("5")</c> is FIVE
    /// DAYS, not five minutes (F2-followup 5).
    /// </summary>
    private static readonly TimeSpan MaxAcceptedTotalDuration = TimeSpan.FromHours(24);

    /// <summary>
    /// Test-only override for <see cref="ResolveMaxTotalDuration"/>. <c>null</c>
    /// (production) reads <c>Adhoc:MaxTotalDuration</c> instead. Nullable rather than a
    /// <c>TimeSpan.Zero</c> sentinel so a test CAN ask for "expire immediately"; note
    /// the DI container does property injection for nobody, so production always sees
    /// <c>null</c>.
    /// </summary>
    internal TimeSpan? MaxTotalDuration { get; init; }

    /// <summary>
    /// Resolves the total budget: the test override, else
    /// <c>Adhoc:MaxTotalDuration</c>, else <see cref="DefaultMaxTotalDuration"/>. A
    /// METHOD, not a property: it reads configuration, so the call site should show
    /// that it is not free — read it once into a local.
    /// <para>
    /// F2-followup 5 — a value outside <c>(0, 24h]</c> is REJECTED with a warning and
    /// the default used, because the realistic misconfiguration is a bare integer that
    /// <see cref="TimeSpan.Parse(string, IFormatProvider)"/> silently reads as DAYS.
    /// Accepting <c>"5"</c> as five days would defeat the entire guarantee this bound
    /// exists to provide.
    /// </para>
    /// </summary>
    private TimeSpan ResolveMaxTotalDuration()
    {
        if (MaxTotalDuration is { } explicitBudget)
        {
            return explicitBudget;
        }

        var configured = config["Adhoc:MaxTotalDuration"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultMaxTotalDuration;
        }
        if (!TimeSpan.TryParse(configured, CultureInfo.InvariantCulture, out var parsed))
        {
            logger.LogWarning(
                "Adhoc:MaxTotalDuration '{Value}' is not a TimeSpan; using {Default}. " +
                "Use a d.hh:mm:ss form such as 00:05:00.", configured, DefaultMaxTotalDuration);
            return DefaultMaxTotalDuration;
        }
        if (parsed <= TimeSpan.Zero || parsed > MaxAcceptedTotalDuration)
        {
            logger.LogWarning(
                "Adhoc:MaxTotalDuration '{Value}' resolves to {Parsed}, outside the accepted " +
                "range (0, {Max}]; using {Default}. NOTE a bare number is parsed as DAYS — " +
                "'5' means 5 days, not 5 minutes; write 00:05:00.",
                configured, parsed, MaxAcceptedTotalDuration, DefaultMaxTotalDuration);
            return DefaultMaxTotalDuration;
        }
        return parsed;
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

        // F2-followup 3 — ONE budget from here, covering the queue wait AND the run,
        // so the script can never still be executing after the server's dispatcher has
        // reported this iteration as timed out. Linked to the host shutdown token too,
        // and disposed on the way out so nothing accumulates on it.
        var budget = ResolveMaxTotalDuration();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
        deadline.CancelAfter(budget);

        // Step 3 (F2): take the machine execution slot unless the target allows
        // parallel task execution. Pre-F2 ad-hoc scripts bypassed the gate outright,
        // so a diagnostic script could run straight into a deployment's file / IIS /
        // service operations on the same box. Bounded so a long-running deployment
        // cannot pin an interactive script indefinitely.
        var slot = await AcquireMachineSlotAsync(command, budget, deadline.Token)
            .ConfigureAwait(false);
        if (slot.Refused)
        {
            return; // AcquireMachineSlotAsync already reported the refusal.
        }

        // Disposing the lease hands the machine to the next queued task; a
        // bypassing script holds none, and disposing null is a no-op.
        using (slot.Lease)
        {
            await RunAndReportAsync(command, budget, deadline.Token).ConfigureAwait(false);
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
    /// F2 — takes the machine execution slot for an ad-hoc script, bounded by the
    /// remaining total budget (<paramref name="deadlineToken"/>). Refuses (and
    /// reports) rather than running late when the slot stays held: a script the
    /// server's dispatcher has already resolved as timed out must not execute
    /// afterwards. Bypasses the gate entirely when the target sets
    /// <see cref="AdhocScriptCommand.AllowParallelTaskExecution"/>.
    /// </summary>
    private async Task<AdhocMachineSlot> AcquireMachineSlotAsync(
        AdhocScriptCommand command, TimeSpan budget, CancellationToken deadlineToken)
    {
        if (command.AllowParallelTaskExecution)
        {
            logger.LogInformation(
                "Adhoc session {SessionId} iter {Iter} bypasses the machine execution gate: " +
                "the target allows parallel task execution.",
                command.SessionId, command.IterNumber);
            return new AdhocMachineSlot(null, false);
        }

        try
        {
            if (await executionGate.TryAcquireNowAsync(deadlineToken)
                    .ConfigureAwait(false) is { } uncontended)
            {
                return new AdhocMachineSlot(uncontended, false);
            }

            logger.LogInformation(
                "Adhoc session {SessionId} iter {Iter} is waiting for the machine execution " +
                "slot (another task is running on this machine); its total budget is {Budget}.",
                command.SessionId, command.IterNumber, budget);

            // The whole remaining budget is available for queueing — but every second
            // spent here is a second the script does NOT get, which is the point: the
            // pair can never exceed the budget the dispatcher is timing against.
            if (await executionGate.AcquireAsync(Timeout.InfiniteTimeSpan, deadlineToken)
                    .ConfigureAwait(false) is { } queued)
            {
                return new AdhocMachineSlot(queued, false);
            }

            // Unreachable: an infinite wait either acquires or throws on the token.
            throw new InvalidOperationException(
                "Unbounded gate acquisition returned without a lease.");
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            // Either the total budget expired while QUEUED, or the host is shutting
            // down (the token is linked to both), or DI disposed the gate first. All
            // three mean the script was NOT executed — report rather than let it
            // escape, because the dispatcher's slot is waiting and this class's
            // contract is that every path closes it.
            logger.LogWarning(
                "Adhoc session {SessionId} iter {Iter} refused: never acquired the machine " +
                "execution slot within its {Budget} budget (or the agent is stopping).",
                command.SessionId, command.IterNumber, budget);
            await ReportFailureAsync(command,
                $"Another task held this machine for the whole {budget} budget (or the agent " +
                "is stopping); the script was NOT executed. Re-run it once the machine is free.")
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
    private async Task RunAndReportAsync(
        AdhocScriptCommand command, TimeSpan budget, CancellationToken deadlineToken)
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
                // F2-followup 3: whatever is LEFT of the total budget. Previously
                // CancellationToken.None, so ScriptRunner's process-tree kill was dead
                // code on this path and a hung script held the machine gate forever.
                ct: deadlineToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Budget expired (or the host is stopping) mid-script. ScriptRunner has
            // already killed the process tree and reaped it; report what we captured so
            // the operator sees partial output rather than a bare server-side timeout.
            logger.LogWarning(
                "Adhoc session {SessionId} iter {Iter} was terminated: it did not finish " +
                "within its {Budget} budget (or the agent is stopping).",
                command.SessionId, command.IterNumber, budget);
            await ReportFailureAsync(command,
                $"The script did not finish within its {budget} budget and was terminated " +
                "(its process tree was killed). Raise Adhoc:MaxTotalDuration on the agent — " +
                "and the server's per-target timeout with it — if the work legitimately " +
                "takes longer.",
                stdout.ToString(), stderr.ToString()).ConfigureAwait(false);
            TryCleanWorkingDir(workingDir);
            return;
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
