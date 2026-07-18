using System.Collections.Concurrent;
using FluentAssertions;
using KrakenDeploy.Agent;
using KrakenDeploy.Agent.Adhoc;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment;
using KrakenDeploy.Agent.Identity;
using KrakenDeploy.Agent.Machine;
using KrakenDeploy.Agent.Services;
using KrakenDeploy.Agent.StepPackages;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// B2/T0-2 — the supervisor must never idle with a dead connection: it retries
/// the initial connect (automatic reconnect does not cover initial start
/// failures), restarts the cycle on a permanent close, re-sends registration on
/// every (re)connect, and distinguishes clean shutdown (stop trying) from failure.
/// </summary>
public sealed class ServerLinkHostedServiceTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private readonly FakeServerLink _link = new();
    private readonly string _dataPath =
        Path.Combine(Path.GetTempPath(), "kraken-supervisor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { /* best effort */ }
    }

    private ServerLinkHostedService CreateService()
    {
        var context = new AgentContext();
        context.SetIdentity(new AgentIdentity
        {
            AgentId = Guid.NewGuid(),
            AgentToken = "test-token",
            ServerUrl = "https://server.example",
            TransportMode = "Reverse",
            ReleaseId = null,
        });

        var agentConfig = Options.Create(new AgentConfig { DataPath = _dataPath });
        var serverOptions = Options.Create(new ServerOptions { Url = "https://server.example" });
        var configuration = new ConfigurationBuilder().Build();

        var deploymentExecutor = new DeploymentExecutor(
            _link,
            new StubPackageSource(),
            new StubArtifactSink(),
            new StepPackageLoader(
                configuration, NullLogger<StepPackageLoader>.Instance, new StubStepPackageSource()),
            agentConfig,
            NullLogger<DeploymentExecutor>.Instance);

        var adhocExecutor = new AdhocScriptExecutor(
            _link, configuration, new StubAdhocInvoker(), NullLogger<AdhocScriptExecutor>.Instance);

        return new ServerLinkHostedService(
            context,
            _link,
            deploymentExecutor,
            adhocExecutor,
            new MachineInfoCollector(NullLogger<MachineInfoCollector>.Instance),
            serverOptions,
            agentConfig,
            TimeProvider.System,
            NullLogger<ServerLinkHostedService>.Instance);
    }

    [Fact]
    public async Task Initial_connect_retries_until_the_server_is_reachable()
    {
        _link.FailStartAttempts = 3;
        var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        try
        {
            await _link.WaitForRegistrationsAsync(1, TestTimeout);

            _link.StartAttempts.Should().Be(4, "three failures + the successful attempt");
            _link.RegisterCalls.Should().Be(1);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Permanent_close_restarts_the_connection_cycle()
    {
        var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        try
        {
            await _link.WaitForRegistrationsAsync(1, TestTimeout);

            await _link.FireClosedAsync(new IOException("server went away for good"));

            // The supervisor must open a fresh connection and re-register.
            await _link.WaitForRegistrationsAsync(2, TestTimeout);
            _link.StartAttempts.Should().Be(2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Reconnected_resends_registration()
    {
        var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        try
        {
            await _link.WaitForRegistrationsAsync(1, TestTimeout);

            await _link.FireReconnectedAsync();

            await _link.WaitForRegistrationsAsync(2, TestTimeout);
            // A reconnect is handled INSIDE the connection — the supervisor must
            // not have restarted it.
            _link.StartAttempts.Should().Be(1);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Registration_failure_is_not_fatal_to_the_cycle()
    {
        _link.FailRegisterAttempts = 1;
        var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        try
        {
            await _link.WaitForRegisterAttemptsAsync(1, TestTimeout);

            // Cycle survived the failed registration: no restart happened…
            _link.StartAttempts.Should().Be(1);

            // …and the next reconnect re-sends it successfully.
            await _link.FireReconnectedAsync();
            await _link.WaitForRegistrationsAsync(1, TestTimeout);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Clean_shutdown_reports_ShuttingDown_and_stops_retrying()
    {
        var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        await _link.WaitForRegistrationsAsync(1, TestTimeout);

        await service.StopAsync(CancellationToken.None);

        _link.StatusReports.Should().Contain("ShuttingDown");
        _link.StopCalls.Should().BeGreaterThan(0);
        _link.StartAttempts.Should().Be(1, "a clean shutdown must not restart the cycle");
    }

    [Fact]
    public async Task Reconnect_refusal_wakes_the_supervisor_instead_of_parking()
    {
        var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        try
        {
            await _link.WaitForRegistrationsAsync(1, TestTimeout);
            _link.StartAttempts.Should().Be(1, "the initial connect was accepted");

            // The server now refuses (e.g. a B6 contract-version gate after an
            // upgrade). The refusal arrives on an automatic reconnect.
            _link.RegistrationResult = new(Accepted: false, AgentContract.CurrentVersion);
            await _link.FireReconnectedAsync();

            // Pre-fix: the OnReconnected handler's StopAsync sets the link's
            // deliberate-stop flag, which SUPPRESSES the Closed event, so the
            // supervision loop parks on its closed signal forever — StartAttempts
            // stays 1 (a zombie agent that reconnected, was refused, never retries).
            // Post-fix: the handler resolves the closed signal itself, waking the
            // loop, which reconnects (StartAttempts → 2), is refused again, and
            // paces on the slow lane.
            await _link.WaitForStartAttemptsAsync(2, TestTimeout);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FakeServerLink : IServerLink
    {
        private readonly List<Func<Exception?, Task>> _closedHandlers = [];
        private readonly List<Func<Task>> _reconnectedHandlers = [];
        private int _startAttempts;
        private int _registerCalls;
        private int _registerAttempts;
        private int _stopCalls;

        public int FailStartAttempts { get; set; }
        public int FailRegisterAttempts { get; set; }

        public int StartAttempts => Volatile.Read(ref _startAttempts);
        public int RegisterCalls => Volatile.Read(ref _registerCalls);
        public int StopCalls => Volatile.Read(ref _stopCalls);
        public ConcurrentQueue<string> StatusReports { get; } = new();

        public bool IsConnected { get; private set; }

        public Task StartAsync(
            string serverUrl, Func<string?> agentJwtProvider, string? releaseId, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _startAttempts);
            if (attempt <= FailStartAttempts)
            {
                throw new IOException($"connection refused (attempt {attempt})");
            }
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _stopCalls);
            IsConnected = false;
            return Task.CompletedTask;
        }

        /// <summary>B6 — the verdict RegisterAsync returns; defaults to accepted.
        /// Set a refusal to drive the slow-lane pacing tests.</summary>
        public AgentRegistrationResult RegistrationResult { get; set; } =
            new(Accepted: true, AgentContract.CurrentVersion);

        public Task<AgentRegistrationResult> RegisterAsync(
            AgentRegistrationRequest request, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _registerAttempts);
            if (attempt <= FailRegisterAttempts)
            {
                throw new InvalidOperationException("registration hub call failed");
            }
            Interlocked.Increment(ref _registerCalls);
            return Task.FromResult(RegistrationResult);
        }

        public Task HeartbeatAsync(HeartbeatRequest request, CancellationToken ct) => Task.CompletedTask;

        public Task ReportStatusAsync(string status, CancellationToken ct)
        {
            StatusReports.Enqueue(status);
            return Task.CompletedTask;
        }

        public Task AppendLogAsync(
            Guid deploymentId, Guid dispatchId, int stepIndex, string level, string message,
            CancellationToken ct)
            => Task.CompletedTask;

        public Task CompleteDeploymentAsync(
            Guid deploymentId, Guid dispatchId, bool success, string? errorMessage, CancellationToken ct)
            => Task.CompletedTask;

        public Task ReportStepCompletedAsync(
            Guid deploymentId, Guid dispatchId, int stepIndex, string stepName, bool success,
            string? errorMessage, IReadOnlyDictionary<string, string> outputVariables,
            IReadOnlyCollection<string> sensitiveOutputNames, CancellationToken ct)
            => Task.CompletedTask;

        public Task ReportAdhocResultAsync(AdhocScriptResult result, CancellationToken ct)
            => Task.CompletedTask;

        public void OnRunDeployment(Func<DeploymentPlan, Task> handler) { }
        public void OnRunAdhocScript(Func<AdhocScriptCommand, Task> handler) { }
        public void OnCancelDeployment(Func<Guid, string?, Task> handler) { }
        public void OnClosed(Func<Exception?, Task> handler) => _closedHandlers.Add(handler);
        public void OnReconnected(Func<Task> handler) => _reconnectedHandlers.Add(handler);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task FireClosedAsync(Exception? ex)
        {
            IsConnected = false;
            foreach (var handler in _closedHandlers)
            {
                await handler(ex);
            }
        }

        public async Task FireReconnectedAsync()
        {
            IsConnected = true;
            foreach (var handler in _reconnectedHandlers)
            {
                await handler();
            }
        }

        public async Task WaitForRegistrationsAsync(int atLeast, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (RegisterCalls < atLeast)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"Expected ≥{atLeast} registrations; saw {RegisterCalls} " +
                        $"(start attempts: {StartAttempts}).");
                }
                await Task.Delay(25);
            }
        }

        public async Task WaitForRegisterAttemptsAsync(int atLeast, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref _registerAttempts) < atLeast)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Expected ≥{atLeast} registration attempts.");
                }
                await Task.Delay(25);
            }
        }

        public async Task WaitForStartAttemptsAsync(int atLeast, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (StartAttempts < atLeast)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"Expected ≥{atLeast} start attempts; saw {StartAttempts} " +
                        "(the supervisor parked instead of retrying).");
                }
                await Task.Delay(25);
            }
        }
    }

    private sealed class StubPackageSource : IPackageSource
    {
        public Task<string> DownloadAsync(
            string packageId, string version, string destDirectory, CancellationToken ct)
            => throw new NotSupportedException("not exercised by supervisor tests");
    }

    private sealed class StubArtifactSink : IArtifactSink
    {
        public Task<string?> UploadAsync(
            Guid deploymentId, string stepName, string filePath, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubStepPackageSource : IStepPackageSource
    {
        public Task EnsureExtractedAsync(string name, string version, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class StubAdhocInvoker : IAdhocScriptInvoker
    {
        public Task<int> InvokeAsync(
            string script, string workingDirectory, IReadOnlyDictionary<string, string> envVars,
            Func<string, string, Task> onOutput, CancellationToken ct)
            => Task.FromResult(0);
    }
}
