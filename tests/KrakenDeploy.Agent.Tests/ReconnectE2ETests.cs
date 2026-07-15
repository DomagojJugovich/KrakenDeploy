using System.Collections.Concurrent;
using FluentAssertions;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// B2/T0-2 acceptance, compressed for CI: the REAL <see cref="SignalRServerLink"/>
/// against a real Kestrel-hosted hub that is STOPPED and RESTARTED on the same
/// port — the in-process equivalent of "stop the server for five minutes with
/// an idle agent, restart it". The agent must reconnect by itself (no process
/// restart), re-fire the OnReconnected hook (the supervisor re-registers
/// through it), and flush the reports it buffered while the server was down,
/// in FIFO order with the DispatchId intact.
/// <para>
/// The real 5-minute outage and the blue-green slot swap remain manual
/// acceptance steps (docs/agent-reconnect.md) — the pacing that makes them
/// pass is pinned by <see cref="AgentReconnectPolicyTests"/>.
/// </para>
/// </summary>
public sealed class ReconnectE2ETests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(45);

    // ── Recording hub ──────────────────────────────────────────────────────

    public sealed class HubCallRecorder
    {
        public ConcurrentQueue<(string Kind, string Detail)> Calls { get; } = new();

        public int Count(string kind) => Calls.Count(c => c.Kind == kind);

        public async Task WaitForAsync(string kind, int atLeast, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Count(kind) < atLeast)
            {
                if (DateTime.UtcNow > deadline)
                {
                    var seen = string.Join(", ", Calls.Select(c => c.Kind));
                    throw new TimeoutException(
                        $"Expected ≥{atLeast} '{kind}' calls; saw [{seen}].");
                }
                await Task.Delay(25);
            }
        }
    }

    public sealed class RecordingAgentHub(HubCallRecorder recorder) : Hub
    {
        public Task RegisterAsync(AgentRegistrationRequest request)
        {
            recorder.Calls.Enqueue(("Register", request.MachineName));
            return Task.CompletedTask;
        }

        public Task HeartbeatAsync(HeartbeatRequest request)
        {
            recorder.Calls.Enqueue(("Heartbeat", request.MachineName ?? ""));
            return Task.CompletedTask;
        }

        public Task ReportStatusAsync(string status)
        {
            recorder.Calls.Enqueue(("Status", status));
            return Task.CompletedTask;
        }

        public Task AppendLogAsync(Guid deploymentId, int stepIndex, string level, string message)
        {
            recorder.Calls.Enqueue(("Log", message));
            return Task.CompletedTask;
        }

        public Task ReportStepCompletedAsync(
            Guid deploymentId, Guid dispatchId, int stepIndex, string stepName, bool success,
            string? errorMessage, Dictionary<string, string> outputVariables,
            List<string> sensitiveOutputNames)
        {
            recorder.Calls.Enqueue(("StepCompleted", $"{stepName}:{dispatchId}"));
            return Task.CompletedTask;
        }

        public Task CompleteDeploymentAsync(Guid deploymentId, Guid dispatchId, bool success, string? errorMessage)
        {
            recorder.Calls.Enqueue(("Completed", $"{deploymentId}:{dispatchId}:{success}"));
            return Task.CompletedTask;
        }

        public Task ReportAdhocResultAsync(AdhocScriptResult result)
        {
            recorder.Calls.Enqueue(("AdhocResult", result.SessionId.ToString()));
            return Task.CompletedTask;
        }
    }

    private static async Task<WebApplication> StartHubHostAsync(int port, HubCallRecorder recorder)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(recorder);
        builder.Services.AddSignalR();

        var app = builder.Build();
        app.MapHub<RecordingAgentHub>("/hubs/agent");
        await app.StartAsync();
        return app;
    }

    private static int BoundPort(WebApplication app) => new Uri(app.Urls.First()).Port;

    // ── The test ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Agent_reconnects_after_server_restart_and_flushes_buffered_reports()
    {
        var recorder = new HubCallRecorder();

        var host = await StartHubHostAsync(port: 0, recorder);
        var port = BoundPort(host);

        var link = new SignalRServerLink(NullLogger<SignalRServerLink>.Instance);

        // Mirror the supervisor's wiring: re-send registration on reconnect.
        var reconnects = 0;
        link.OnReconnected(async () =>
        {
            Interlocked.Increment(ref reconnects);
            await link.RegisterAsync(Registration("after-reconnect"), CancellationToken.None);
        });

        try
        {
            await link.StartAsync(
                $"http://127.0.0.1:{port}", () => "test-token", releaseId: null, CancellationToken.None);
            await link.RegisterAsync(Registration("initial"), CancellationToken.None);
            await recorder.WaitForAsync("Register", 1, TestTimeout);

            // ── Server goes away (deploy/restart/blip) ────────────────────
            await StopHostAsync(host);

            await WaitUntilAsync(() => !link.IsConnected, TestTimeout,
                "the link must observe the connection loss");

            // Reports produced while the server is down are buffered, not lost.
            var deploymentId = Guid.NewGuid();
            var dispatchId = Guid.NewGuid();
            await link.AppendLogAsync(deploymentId, 0, "info", "offline line", CancellationToken.None);
            await link.ReportStepCompletedAsync(
                deploymentId, dispatchId, 0, "Deploy", success: true, errorMessage: null,
                outputVariables: new Dictionary<string, string> { ["Url"] = "https://x" },
                sensitiveOutputNames: [], CancellationToken.None);
            await link.CompleteDeploymentAsync(
                deploymentId, dispatchId, success: true, errorMessage: null, CancellationToken.None);

            recorder.Calls.Clear();

            // ── Server comes back on the SAME port ────────────────────────
            host = await StartHubHostAsync(port, recorder);

            // The agent reconnects on its own — no process restart, no StartAsync.
            await WaitUntilAsync(() => link.IsConnected, TestTimeout,
                "the unbounded retry policy must reconnect once the server is back");

            // Registration re-sent through the OnReconnected hook…
            await recorder.WaitForAsync("Register", 1, TestTimeout);
            reconnects.Should().BeGreaterThan(0);

            // …and the buffered reports flushed, strictly in FIFO order.
            await recorder.WaitForAsync("Completed", 1, TestTimeout);
            var flushed = recorder.Calls.Where(c => c.Kind is "Log" or "StepCompleted" or "Completed").ToList();
            flushed.Select(c => c.Kind).Should().Equal(["Log", "StepCompleted", "Completed"]);
            flushed[1].Detail.Should().Be($"Deploy:{dispatchId}");
            flushed[2].Detail.Should().Be($"{deploymentId}:{dispatchId}:True");
        }
        finally
        {
            // Dispose the LINK first so the host has no live connection to
            // wait on — otherwise each Stop/Dispose can burn the full host
            // ShutdownTimeout and the test takes a silent extra minute.
            await link.DisposeAsync();
            await StopHostAsync(host);
        }
    }

    /// <summary>Stop with a short deadline: an open agent connection must never
    /// hold the test hostage for the host's default shutdown timeout.</summary>
    private static async Task StopHostAsync(WebApplication host)
    {
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await host.StopAsync(stopCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Deadline hit — Kestrel aborts remaining connections; fine for tests.
        }
        await host.DisposeAsync();
    }

    private static AgentRegistrationRequest Registration(string marker) => new(
        TargetId: Guid.NewGuid(),
        MachineName: marker,
        OperatingSystem: "TestOS",
        AgentVersion: "0.0-test",
        Roles: [],
        FreeDiskBytes: 0,
        TotalRamBytes: 0);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not reached within {timeout}: {because}");
            }
            await Task.Delay(50);
        }
    }
}
