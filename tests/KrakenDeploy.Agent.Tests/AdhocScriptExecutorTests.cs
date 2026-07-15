using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Agent.Adhoc;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Unit tests for M11.E.7 — <see cref="AdhocScriptExecutor"/>. Focuses on the
/// security-critical fail-closed paths (signature mismatch, missing key,
/// malformed key) and the happy-path verify-then-run + report.
/// </summary>
public sealed class AdhocScriptExecutorTests
{
    private static (RSA Private, string PublicPem) NewKeyPair()
    {
        var priv = RSA.Create(2048);
        return (priv, priv.ExportSubjectPublicKeyInfoPem());
    }

    private static IConfiguration ConfigWithKey(string? pem)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Adhoc:TrustedPublicKey"] = pem })
            .Build();

    private static IConfiguration EmptyConfig()
        => new ConfigurationBuilder().Build();

    // ── Fail-closed paths ───────────────────────────────────────────────────

    [Fact]
    public async Task Refuses_when_no_trusted_public_key_is_configured()
    {
        var serverLink = new RecordingServerLink();
        var executor = new AdhocScriptExecutor(
            serverLink, EmptyConfig(), new NeverInvokedRunner(),
            NullLogger<AdhocScriptExecutor>.Instance);

        await executor.HandleAsync(new AdhocScriptCommand(
            Guid.NewGuid(), 1, "Get-Date", "AAAA"));

        serverLink.LastResult.Should().NotBeNull();
        serverLink.LastResult!.AgentError.Should().Contain("no Adhoc:TrustedPublicKey");
        serverLink.LastResult.ExitCode.Should().Be(-1);
    }

    [Fact]
    public async Task Refuses_when_configured_key_is_malformed()
    {
        var serverLink = new RecordingServerLink();
        var executor = new AdhocScriptExecutor(
            serverLink, ConfigWithKey("not-a-pem-string"),
            new NeverInvokedRunner(),
            NullLogger<AdhocScriptExecutor>.Instance);

        await executor.HandleAsync(new AdhocScriptCommand(
            Guid.NewGuid(), 1, "Get-Date", "AAAA"));

        serverLink.LastResult.Should().NotBeNull();
        serverLink.LastResult!.AgentError.Should().Contain("Adhoc:TrustedPublicKey");
    }

    [Fact]
    public async Task Refuses_tampered_script_without_running_it()
    {
        var (priv, pem) = NewKeyPair();
        using (priv)
        {
            var sessionId = Guid.NewGuid();
            var goodSig = AdhocScriptSigner.Sign(sessionId, 1, "Get-Date", priv);

            var serverLink = new RecordingServerLink();
            var runner = new NeverInvokedRunner();
            var executor = new AdhocScriptExecutor(
                serverLink, ConfigWithKey(pem), runner,
                NullLogger<AdhocScriptExecutor>.Instance);

            // Same signature, but the script bytes have changed by one char.
            await executor.HandleAsync(new AdhocScriptCommand(
                sessionId, 1, "Get-Date ", goodSig));

            serverLink.LastResult!.AgentError.Should().Contain("Signature verification failed");
            runner.WasInvoked.Should().BeFalse(
                "the script MUST NOT execute when the signature doesn't match");
        }
    }

    [Fact]
    public async Task Refuses_replay_of_signature_from_a_different_iteration()
    {
        var (priv, pem) = NewKeyPair();
        using (priv)
        {
            var sessionId = Guid.NewGuid();
            var iter2Sig = AdhocScriptSigner.Sign(sessionId, 2, "Get-Date", priv);

            var serverLink = new RecordingServerLink();
            var runner = new NeverInvokedRunner();
            var executor = new AdhocScriptExecutor(
                serverLink, ConfigWithKey(pem), runner,
                NullLogger<AdhocScriptExecutor>.Instance);

            // Replay iter-2 signature as iter-3 — must fail.
            await executor.HandleAsync(new AdhocScriptCommand(
                sessionId, 3, "Get-Date", iter2Sig));

            serverLink.LastResult!.AgentError.Should().Contain("Signature verification failed");
            runner.WasInvoked.Should().BeFalse();
        }
    }

    // ── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Valid_signature_runs_script_and_reports_collated_result()
    {
        var (priv, pem) = NewKeyPair();
        using (priv)
        {
            var sessionId = Guid.NewGuid();
            var sig = AdhocScriptSigner.Sign(sessionId, 1, "Get-Date", priv);

            var serverLink = new RecordingServerLink();
            var runner = new CannedRunner(exitCode: 0,
                stdoutLines: ["line-1", "line-2"], stderrLines: ["a-warning"]);

            var executor = new AdhocScriptExecutor(
                serverLink, ConfigWithKey(pem), runner,
                NullLogger<AdhocScriptExecutor>.Instance);

            await executor.HandleAsync(new AdhocScriptCommand(
                sessionId, 1, "Get-Date", sig));

            var result = serverLink.LastResult;
            result.Should().NotBeNull();
            result!.AgentError.Should().BeNull();
            result.ExitCode.Should().Be(0);
            result.Stdout.Should().Contain("line-1").And.Contain("line-2");
            result.Stderr.Should().Contain("a-warning");
            result.SessionId.Should().Be(sessionId);
            result.IterNumber.Should().Be(1);
        }
    }

    [Fact]
    public async Task Reports_AgentError_when_runner_throws()
    {
        var (priv, pem) = NewKeyPair();
        using (priv)
        {
            var sessionId = Guid.NewGuid();
            var sig = AdhocScriptSigner.Sign(sessionId, 1, "Get-Date", priv);
            var serverLink = new RecordingServerLink();
            var executor = new AdhocScriptExecutor(
                serverLink, ConfigWithKey(pem),
                new ThrowingRunner(new InvalidOperationException("pwsh missing")),
                NullLogger<AdhocScriptExecutor>.Instance);

            await executor.HandleAsync(new AdhocScriptCommand(
                sessionId, 1, "Get-Date", sig));

            serverLink.LastResult!.AgentError.Should().Contain("Script execution threw");
        }
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed class RecordingServerLink : IServerLink
    {
        public AdhocScriptResult? LastResult { get; private set; }

        public Task ReportAdhocResultAsync(AdhocScriptResult result, CancellationToken ct)
        {
            LastResult = result;
            return Task.CompletedTask;
        }

        // Rest of IServerLink — unused by the executor.
        public bool IsConnected => true;
        public Task StartAsync(string serverUrl, Func<string?> agentJwtProvider, string? releaseId, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RegisterAsync(AgentRegistrationRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task HeartbeatAsync(HeartbeatRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task ReportStatusAsync(string status, CancellationToken ct) => Task.CompletedTask;
        public Task AppendLogAsync(Guid deploymentId, int stepIndex, string level, string message, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteDeploymentAsync(Guid deploymentId, bool success, string? errorMessage, CancellationToken ct) => Task.CompletedTask;
        public Task ReportStepCompletedAsync(Guid deploymentId, int stepIndex, string stepName, bool success,
            string? errorMessage, IReadOnlyDictionary<string, string> outputVariables,
            IReadOnlyCollection<string> sensitiveOutputNames, CancellationToken ct) => Task.CompletedTask;
        public void OnRunDeployment(Func<DeploymentPlan, Task> handler) { }
        public void OnRunAdhocScript(Func<AdhocScriptCommand, Task> handler) { }
        public void OnClosed(Func<Exception?, Task> handler) { }
        public void OnReconnected(Func<Task> handler) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NeverInvokedRunner : IAdhocScriptInvoker
    {
        public bool WasInvoked { get; private set; }
        public Task<int> InvokeAsync(
            string script, string workingDirectory,
            IReadOnlyDictionary<string, string> envVars,
            Func<string, string, Task> onOutput, CancellationToken ct)
        {
            WasInvoked = true;
            throw new InvalidOperationException(
                "Runner must not be invoked on a fail-closed refuse path.");
        }
    }

    private sealed class CannedRunner(
        int exitCode, string[] stdoutLines, string[] stderrLines) : IAdhocScriptInvoker
    {
        public async Task<int> InvokeAsync(
            string script, string workingDirectory,
            IReadOnlyDictionary<string, string> envVars,
            Func<string, string, Task> onOutput, CancellationToken ct)
        {
            foreach (var line in stdoutLines) { await onOutput("info", line); }
            foreach (var line in stderrLines) { await onOutput("error", line); }
            return exitCode;
        }
    }

    private sealed class ThrowingRunner(Exception ex) : IAdhocScriptInvoker
    {
        public Task<int> InvokeAsync(
            string script, string workingDirectory,
            IReadOnlyDictionary<string, string> envVars,
            Func<string, string, Task> onOutput, CancellationToken ct)
            => Task.FromException<int>(ex);
    }
}
