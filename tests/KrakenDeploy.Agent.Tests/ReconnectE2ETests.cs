using System.Collections.Concurrent;
using FluentAssertions;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.Adhoc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
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
        public Task<AgentRegistrationResult> RegisterAsync(AgentRegistrationRequest request)
        {
            recorder.Calls.Enqueue(("Register", request.MachineName));
            return Task.FromResult(
                new AgentRegistrationResult(Accepted: true, AgentContract.CurrentVersion));
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

        public Task AppendLogAsync(
            Guid deploymentId, Guid dispatchId, int stepIndex, string level, string message)
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
            await link.AppendLogAsync(deploymentId, dispatchId, 0, "info", "offline line", CancellationToken.None);
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

    // ── Framework behaviour these designs rest on ──────────────────────────
    //
    // Both tests below assert what SignalR DOES, not what KrakenDeploy does. They exist
    // because three consecutive review rounds of this work package shipped fixes whose
    // premise about this exact behaviour was wrong, in both directions — a pacing delay put
    // where nothing could wake it, then that delay deleted on the grounds that nothing could
    // wake it either. If a framework upgrade changes either answer, the pacing design in
    // ServerLinkHostedService and AgentReconnectPolicy needs revisiting, and these are what
    // will say so.

    [Theory]
    [InlineData(true)]   // OnConnectedAsync throws (e.g. a saturated tenant database)
    [InlineData(false)]  // OnConnectedAsync calls Context.Abort() (unknown / retired target)
    public async Task A_server_side_rejection_fires_Closed_and_never_reconnects(bool byThrowing)
    {
        // The load-bearing facts, in order:
        //   1. StartAsync SUCCEEDS. The handshake completes before the hub's
        //      OnConnectedAsync runs, so a rejection there is not an initial-connect failure
        //      and the supervisor's connect-lane pacing never sees it.
        //   2. Closed FIRES — so the supervision loop's park DOES release, which is what
        //      makes the loop the right place to pace this.
        //   3. Reconnecting NEVER fires and the retry policy is NEVER consulted — so a
        //      counter fed from the Reconnecting event (the deleted "churn lane") could not
        //      observe this failure at all, in either the throw or the Abort shape.
        var recorder = new HubCallRecorder();
        var events = new ConcurrentQueue<string>();
        var policyConsulted = 0;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(recorder);
        builder.Services.AddSingleton(new RejectionMode(byThrowing));
        builder.Services.AddSignalR();
        var host = builder.Build();
        host.MapHub<RejectingHub>("/hubs/agent");
        await host.StartAsync();

        var connection = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{BoundPort(host)}/hubs/agent")
            .WithAutomaticReconnect(new CountingRetryPolicy(() => Interlocked.Increment(ref policyConsulted)))
            .Build();
        connection.Reconnecting += _ => { events.Enqueue("Reconnecting"); return Task.CompletedTask; };
        connection.Closed += _ => { events.Enqueue("Closed"); return Task.CompletedTask; };

        try
        {
            // Fact 1 is proven by StartAsync returning instead of throwing. Do NOT
            // additionally assert State == Connected here: the server-side rejection
            // tears the connection down concurrently with StartAsync completing, and
            // on loopback the close can win that race (observed deterministically in
            // CI for the Abort shape) — the state is legitimately transient. The
            // load-bearing claim is only that the rejection never presents as an
            // initial-connect failure, i.e. StartAsync does not throw.
            await connection.StartAsync();

            await WaitUntilAsync(() => events.Contains("Closed"), TestTimeout,
                "a server-side rejection must surface as a permanent close");

            events.Should().NotContain("Reconnecting",
                "automatic reconnect is never engaged, so nothing fed from that event can " +
                "pace this failure");
            policyConsulted.Should().Be(0, "the retry policy is never consulted either");
            connection.State.Should().Be(HubConnectionState.Disconnected);
        }
        finally
        {
            await connection.DisposeAsync();
            await StopHostAsync(host);
        }
    }

    [Fact]
    public async Task A_transport_drop_computes_the_retry_delay_before_raising_Reconnecting()
    {
        // The second half of why the churn lane went. For the drop it COULD observe,
        // HubConnection.ReconnectAsync calls GetNextRetryDelay(...) and only then raises
        // Reconnecting (fire-and-forget). A counter incremented from that event therefore
        // lagged the delay it was meant to pace by a whole episode: the first attempt read
        // zero and returned TimeSpan.Zero, so production emitted 0, 1s, 2s, 4s while the
        // test asserted 1s, 2s, 4s, 8s.
        var recorder = new HubCallRecorder();
        var order = new ConcurrentQueue<string>();

        var host = await StartHubHostAsync(port: 0, recorder);
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{BoundPort(host)}/hubs/agent")
            .WithAutomaticReconnect(new CountingRetryPolicy(
                () => order.Enqueue("NextRetryDelay"), TimeSpan.FromMilliseconds(200)))
            .Build();
        connection.Reconnecting += _ => { order.Enqueue("Reconnecting"); return Task.CompletedTask; };

        try
        {
            await connection.StartAsync();
            await StopHostAsync(host);   // a genuine transport drop, not a rejection

            await WaitUntilAsync(() => order.Count >= 2, TestTimeout,
                "the client must both consult the policy and raise Reconnecting");

            order.Take(2).Should().Equal(["NextRetryDelay", "Reconnecting"],
                "the delay is computed BEFORE the event that would have updated the counter");
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>Which shape of server-side rejection <see cref="RejectingHub"/> performs.</summary>
    public sealed record RejectionMode(bool ByThrowing);

    public sealed class RejectingHub(RejectionMode mode) : Hub
    {
        public override Task OnConnectedAsync()
        {
            if (mode.ByThrowing)
            {
                throw new InvalidOperationException("tenant database unavailable");
            }
            Context.Abort();
            return Task.CompletedTask;
        }
    }

    /// <summary>Records that the client consulted the retry policy, and when.</summary>
    private sealed class CountingRetryPolicy(Action onConsulted, TimeSpan? delay = null) : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            onConsulted();
            return delay ?? TimeSpan.FromMilliseconds(200);
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
        FreeDiskBytes: 0,
        TotalRamBytes: 0,
        ContractVersion: AgentContract.CurrentVersion);

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
