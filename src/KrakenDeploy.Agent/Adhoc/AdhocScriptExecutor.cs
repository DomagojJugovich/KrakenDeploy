using System.Security.Cryptography;
using System.Text;
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
    ILogger<AdhocScriptExecutor> logger)
{
    /// <summary>
    /// Working directory the script runs in. Created lazily under the agent's
    /// staging tree; cleaned up after the run.
    /// </summary>
    private static readonly string StagingRoot =
        Path.Combine(Path.GetTempPath(), "kraken-adhoc");

    /// <summary>Entry point — wire this to <c>IServerLink.OnRunAdhocScript</c>.</summary>
    public async Task HandleAsync(AdhocScriptCommand command)
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

        // Step 3: execute via ScriptRunner; capture stdout / stderr / exit code.
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var workingDir = Path.Combine(StagingRoot, command.SessionId.ToString("N"),
            command.IterNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                    ["KrakenAdhocIterNumber"] = command.IterNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
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

        // Step 4: report.
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
