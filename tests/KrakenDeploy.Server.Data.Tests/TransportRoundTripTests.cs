using System.Text;
using System.Threading.Channels;
using FluentAssertions;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment;
using KrakenDeploy.Agent.Services;
using KrakenDeploy.Agent.StepPackages;
using KrakenDeploy.Agent.Transport;
using KrakenDeploy.Contracts;
using KrakenDeploy.Contracts.StepPackages;
using KrakenDeploy.Contracts.Steps;
using KrakenDeploy.Server.Core.Domain.Deployments;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Data.Tests.OrchestratorHarness;
using KrakenDeploy.Server.Services;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// B8 — the server↔agent transport ROUND TRIP over real SignalR. Every other
/// suite fakes one side of the wire (<c>FakeAgentHubContext</c> server-side,
/// <c>RecordingAgentHub</c> agent-side), so a plan-serialization or hub-contract
/// drift — exactly what B6's wire pass risks — passes all of them. Here the
/// REAL <see cref="AgentHub"/> (with the production AgentJwt validation chain,
/// including the A8 atv revocation check) is hosted on loopback Kestrel, a REAL
/// <see cref="SignalRServerLink"/> + <see cref="DeploymentExecutor"/> connects
/// to it over WebSocket, and the REAL <see cref="DeploymentWorker"/> dispatches
/// seeded deployments through the shared registries.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class TransportRoundTripTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Deployment_round_trips_over_real_SignalR()
    {
        await using var seeder = new OrchestratorTestHarness(postgres);
        await using var host = await RoundTripHost.StartAsync(postgres);

        var (deploymentId, target) = await SeedTwoStepDeploymentAsync(
            seeder,
            RoundTripSteps.Produce("produce"),
            RoundTripSteps.Consume("consume", "Octopus.Action[produce].Output.Url"));
        await host.ConnectRealAgentAsync(target);

        await host.RunDeploymentAsync(deploymentId).WaitAsync(TestTimeout);

        // The consume step SUCCEEDING is the B4 guard: it returns false unless
        // the second wave's sub-plan — built server-side, serialized over the
        // real wire — carried the first step's captured output.
        (await seeder.GetDeploymentAsync(deploymentId)).Status
            .Should().Be(DeploymentStatus.Succeeded,
                "both steps ran on the real agent and the step-2 sub-plan carried step-1's output");

        await using var db = seeder.CreateContext();
        var log = await TaskLogService.ReadAllAsync(db, deploymentId);
        log.Should().Contain(l => l.Message.Contains("round-trip-log-line"),
            "the agent's AppendLog leg must land in the task log over the real wire");

        (await db.TaskOutputVariables.IgnoreQueryFilters()
                .Where(v => v.TaskId == deploymentId && v.Name == "Url")
                .Select(v => v.Value)
                .FirstOrDefaultAsync())
            .Should().Be("https://round-trip",
                "the captured output must persist via the real ReportStepCompleted leg");
    }

    [Fact]
    public async Task Server_side_step_feeds_the_real_agent_over_the_wire()
    {
        await using var seeder = new OrchestratorTestHarness(postgres);
        await using var host = await RoundTripHost.StartAsync(postgres);

        // Wave 1 runs ON THE SERVER (real shell via ServerScriptStepRunner) and
        // captures an output; wave 2 is dispatched to the REAL agent, whose
        // sub-plan must carry that server-side capture.
        var (syntax, body, edition) = OperatingSystem.IsWindows()
            ? ("PowerShell", ServerProduceBodyPowerShell, "Desktop")
            : ("Bash", ServerProduceBodyBash, (string?)null);
        var serverConfig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Octopus.Action.Script.ScriptBody"] = body,
            ["Octopus.Action.Script.Syntax"]     = syntax,
        };
        if (edition is not null)
        {
            serverConfig["Octopus.Action.PowerShell.Edition"] = edition;
        }

        var (deploymentId, target) = await SeedTwoStepDeploymentAsync(
            seeder,
            // D3 — RunOnServer is a typed flag now, not a Config key.
            new StepBuilder { Name = "srv", StepType = "Octopus.Script", RunOnServer = true, Config = serverConfig },
            RoundTripSteps.Consume("consume", "Octopus.Action[srv].Output.Url"));
        await host.ConnectRealAgentAsync(target);

        await host.RunDeploymentAsync(deploymentId).WaitAsync(TestTimeout);

        (await seeder.GetDeploymentAsync(deploymentId)).Status
            .Should().Be(DeploymentStatus.Succeeded,
                "the agent-side consume step read the server-side step's capture over the real wire");
    }

    [Fact]
    public async Task Agent_disconnect_mid_deployment_reaches_terminal()
    {
        await using var seeder = new OrchestratorTestHarness(postgres);
        // Short disconnect grace so the B3 monitor fires fast; generous wave
        // ceiling so the DEADLINE is provably not what un-hangs the test.
        await using var host = await RoundTripHost.StartAsync(postgres, new EngineOptions
        {
            AgentDisconnectWaveGrace = TimeSpan.FromSeconds(2),
            MaxTargetWaveDuration    = TimeSpan.FromMinutes(5),
        });

        var (deploymentId, target) = await SeedTwoStepDeploymentAsync(
            seeder,
            RoundTripSteps.Block("hang"),
            RoundTripSteps.Produce("never-reached"));
        var agent = await host.ConnectRealAgentAsync(target);

        var dispatch = host.RunDeploymentAsync(deploymentId);
        try
        {
            await WaitUntilAsync(() => agent.Executor.IsExecuting,
                "the real agent must have received the wave and be executing");

            // Hard-drop the connection mid-step: the hub's OnDisconnectedAsync
            // removes the registry entry, and the B3 worker-side monitor must
            // cancel the wave after the grace — the deployment goes terminal
            // instead of awaiting a dead agent forever.
            await agent.Link.DisposeAsync();

            await dispatch.WaitAsync(TestTimeout);
        }
        finally
        {
            // Unwind the deliberately-blocked handler so no task leaks.
            agent.Executor.TryCancel(deploymentId, "test teardown");
        }

        var status = (await seeder.GetDeploymentAsync(deploymentId)).Status;
        status.IsTerminal().Should().BeTrue(
            $"a mid-deployment agent drop must reach a terminal status, got {status}");
        status.Should().Be(DeploymentStatus.Failed);
    }

    // ── F2-followup 1: the agent must accept a SECOND push while one is running ──
    //
    // These three are the only tests in the repo that can observe the defect: every
    // other suite either fakes the agent side (so the SignalR client's single-reader
    // dispatch loop is absent) or dispatches exactly one deployment per agent.

    [Fact]
    public async Task Second_deployment_to_the_same_machine_waits_for_the_gate()
    {
        await using var seeder = new OrchestratorTestHarness(postgres);
        await using var host = await RoundTripHost.StartAsync(postgres, new EngineOptions
        {
            // Nothing server-side may un-hang this: the gate is the only thing that
            // should hold the second deployment, and the release is the only thing
            // that should let it through.
            MaxTargetWaveDuration    = TimeSpan.FromMinutes(5),
            AgentDisconnectWaveGrace = TimeSpan.Zero,
        });

        var (blocking, quick, target) = await SeedTwoProjectsOneTargetAsync(seeder);
        var agent = await host.ConnectRealAgentAsync(target);

        var blockingRun = host.RunDeploymentAsync(blocking);
        try
        {
            await WaitUntilAsync(() => agent.Executor.IsExecuting,
                "the blocking deployment must be executing and holding the machine gate");
            await WaitUntilAsync(() => agent.Gate.IsHeld,
                "the gate must actually be held, not merely registered in flight");

            // The second push IS delivered now (pre-fix it was not: the client awaited
            // the first handler), so the plan reaches the agent and queues on the gate.
            var quickRun = host.RunDeploymentAsync(quick);
            await Task.Delay(TimeSpan.FromSeconds(2));
            quickRun.IsCompleted.Should().BeFalse(
                "the second deployment must be QUEUED behind the first on the machine gate");
            (await seeder.GetOutcomesAsync(quick)).Should().BeEmpty(
                "a queued plan must not have run any step yet");

            // Release the holder; the queued plan then inherits the machine.
            agent.Executor.TryCancel(blocking, "test releases the machine");
            await blockingRun.WaitAsync(TestTimeout);
            await quickRun.WaitAsync(TestTimeout);

            (await seeder.GetDeploymentAsync(quick)).Status
                .Should().Be(DeploymentStatus.Succeeded,
                    "once the gate frees, the queued deployment runs to completion");

            // The load-bearing assertion. "Did not complete" alone is satisfied by an
            // UNDELIVERED plan, which is exactly the pre-fix behaviour — so prove the
            // plan reached the agent and queued on the gate, by its own task log.
            await using var db = seeder.CreateContext();
            var quickLog = await TaskLogService.ReadAllAsync(db, quick);
            quickLog.Should().Contain(
                l => l.Message.Contains("Waiting for other work to finish on this machine"),
                "the second plan must have been DELIVERED and queued on the machine gate — "
                + "pre-fix it was never dispatched to the agent at all");
        }
        finally
        {
            agent.Executor.TryCancel(blocking, "test teardown");
            await blockingRun.WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task Parallel_flag_lets_a_second_deployment_co_run_while_the_first_blocks()
    {
        await using var seeder = new OrchestratorTestHarness(postgres);
        await using var host = await RoundTripHost.StartAsync(postgres, new EngineOptions
        {
            MaxTargetWaveDuration    = TimeSpan.FromMinutes(5),
            AgentDisconnectWaveGrace = TimeSpan.Zero,
        });

        var (blocking, quick, target) = await SeedTwoProjectsOneTargetAsync(seeder);
        // The ONLY difference from the test above.
        await seeder.SetAllowParallelTaskExecutionAsync(target.Id, true);
        var agent = await host.ConnectRealAgentAsync(target);

        var blockingRun = host.RunDeploymentAsync(blocking);
        try
        {
            await WaitUntilAsync(() => agent.Executor.IsExecuting,
                "the blocking deployment must be executing");
            await WaitUntilAsync(() => agent.Gate.ReaderCount == 1,
                "F5 — the flag takes the SHARED side of the gate, it does not skip it");

            // Both plans hit a target that opted in, so both hold SHARED leases and the
            // second completes WHILE the first is still blocked mid-step. Impossible
            // unless the agent accepts a second push concurrently (F2-followup 1).
            await host.RunDeploymentAsync(quick).WaitAsync(TestTimeout);

            (await seeder.GetDeploymentAsync(quick)).Status
                .Should().Be(DeploymentStatus.Succeeded);
            blockingRun.IsCompleted.Should().BeFalse(
                "the first deployment is still blocked — the second co-ran past it");

            // F5 — pre-F5 this asserted IsHeld == false, because a flagged plan took no
            // lease at all. It now holds a real reader, and the co-runner's release must
            // have decremented to exactly that one rather than zeroing the count.
            agent.Gate.ReaderCount.Should().Be(1,
                "the blocking plan still holds its shared lease; the co-runner released "
                + "only its own");
            agent.Gate.IsWriteHeld.Should().BeFalse(
                "neither plan is exclusive, so no writer may be recorded");
        }
        finally
        {
            agent.Executor.TryCancel(blocking, "test teardown");
            await blockingRun.WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task Operator_cancel_push_reaches_a_running_deployment()
    {
        // B6's cooperative abort travelled the same broken path: the cancel push was
        // queued behind the deployment it targeted and arrived after the run ended, so
        // TryCancel found nothing in flight and the process-tree kill never fired.
        // Here the ONLY thing that can end this deployment is the pushed cancel — the
        // test never calls TryCancel on the success path.
        await using var seeder = new OrchestratorTestHarness(postgres);
        await using var host = await RoundTripHost.StartAsync(postgres, new EngineOptions
        {
            MaxTargetWaveDuration    = TimeSpan.FromMinutes(5),
            AgentDisconnectWaveGrace = TimeSpan.Zero,
        });

        var (deploymentId, target) = await SeedTwoStepDeploymentAsync(
            seeder, RoundTripSteps.Block("hang"), RoundTripSteps.Produce("never-reached"));
        var agent = await host.ConnectRealAgentAsync(target);

        var dispatch = host.RunDeploymentAsync(deploymentId);
        try
        {
            await WaitUntilAsync(() => agent.Executor.IsExecuting,
                "the real agent must be executing before the cancel is pushed");

            await host.CancelDeploymentAsync(deploymentId);

            await dispatch.WaitAsync(TestTimeout);
            (await seeder.GetDeploymentAsync(deploymentId)).Status
                .Should().Be(DeploymentStatus.Cancelled);
            await WaitUntilAsync(() => !agent.Executor.IsExecuting,
                "the pushed cancel must have aborted the in-flight run on the agent");
        }
        finally
        {
            agent.Executor.TryCancel(deploymentId, "test teardown");
        }
    }

    // ── Seeding ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Two deployments in DIFFERENT projects aimed at ONE target: the first blocks
    /// mid-step, the second is a one-step no-op. Different projects on purpose — F1
    /// serializes same-(project, environment, tenant) deployments at claim time, so
    /// same-project deployments would never both reach the agent and the test would
    /// pass for the wrong reason.
    /// </summary>
    private static async Task<(Guid Blocking, Guid Quick, DeploymentTarget Target)>
        SeedTwoProjectsOneTargetAsync(OrchestratorTestHarness seeder)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var env = await seeder.SeedEnvironmentAsync($"rt-e-{tag}");
        var targets = await seeder.SeedTargetsAsync($"rt-t-{tag}");

        var blockingProject = await seeder.SeedProjectAsync($"rt-block-{tag}");
        var blockingRelease = await seeder.SeedReleaseAsync(
            blockingProject.Id, "1.0", RoundTripSteps.Block("hang"));
        var blocking = await seeder.CreateDeploymentAsync(blockingRelease.Id, env.Id, targets);

        var quickProject = await seeder.SeedProjectAsync($"rt-quick-{tag}");
        var quickRelease = await seeder.SeedReleaseAsync(
            quickProject.Id, "1.0", RoundTripSteps.Produce("fast"));
        var quick = await seeder.CreateDeploymentAsync(quickRelease.Id, env.Id, targets);

        return (blocking, quick, targets[0]);
    }

    private static async Task<(Guid DeploymentId, DeploymentTarget Target)> SeedTwoStepDeploymentAsync(
        OrchestratorTestHarness seeder, StepBuilder step1, StepBuilder step2)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var project = await seeder.SeedProjectAsync($"rt-p-{tag}");
        var env = await seeder.SeedEnvironmentAsync($"rt-e-{tag}");
        var targets = await seeder.SeedTargetsAsync($"rt-t-{tag}");
        var release = await seeder.SeedReleaseAsync(project.Id, "1.0", step1, step2);
        var deploymentId = await seeder.CreateDeploymentAsync(release.Id, env.Id, targets);
        return (deploymentId, targets[0]);
    }

    private const string ServerProduceBodyPowerShell =
        "$n=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('Url'));" +
        "$v=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('https://round-trip'));" +
        "Write-Host \"##octopus[setVariable name='$n' value='$v']\"";

    private const string ServerProduceBodyBash =
        "n=$(printf %s Url | base64); v=$(printf %s https://round-trip | base64); " +
        "echo \"##octopus[setVariable name='$n' value='$v']\"";

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(because);
            }
            await Task.Delay(25);
        }
    }
}

/// <summary>Builders for steps handled by <see cref="RoundTripStepHandler"/> —
/// pinned to the staged test step package so the REAL agent resolves them.</summary>
internal static class RoundTripSteps
{
    public const string PackageId = "kraken.roundtrip";
    public const string PackageVersion = "1.0.0";
    public const string StepType = "Kraken.RoundTrip";

    public static StepBuilder Produce(string name) => Build(name, "produce", null);
    public static StepBuilder Consume(string name, string expectKey) => Build(name, "consume", expectKey);
    public static StepBuilder Block(string name) => Build(name, "block", null);

    private static StepBuilder Build(string name, string mode, string? expectKey)
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["roundtrip.mode"] = mode,
        };
        if (expectKey is not null)
        {
            config["roundtrip.expectKey"] = expectKey;
        }
        return new StepBuilder
        {
            Name               = name,
            StepType           = StepType,
            Config             = config,
            StepPackageName    = PackageId,
            StepPackageVersion = PackageVersion,
        };
    }
}

/// <summary>
/// The test step handler the REAL agent executes — this assembly is staged as
/// the step package's executor (the <c>SamplePluginStepHandler</c> loader
/// pattern), so the loader activates it inside its plugin ALC. Public +
/// parameterless by contract.
/// </summary>
public sealed class RoundTripStepHandler : IStepHandler
{
    public bool CanHandle(string stepType)
        => stepType.Equals(RoundTripSteps.StepType, StringComparison.OrdinalIgnoreCase);

    public bool RequiresPackage => false;

    public async Task<bool> HandleAsync(StepHandlerContext context, CancellationToken ct)
    {
        var mode = context.Step.Config.GetValueOrDefault("roundtrip.mode", "produce");
        switch (mode)
        {
            case "produce":
                await context.LogAsync("info", "round-trip-log-line").ConfigureAwait(false);
                await context.LogAsync("info", SetVariableMarker("Url", "https://round-trip"))
                    .ConfigureAwait(false);
                return true;

            case "consume":
                var key = context.Step.Config["roundtrip.expectKey"];
                if (context.Plan.Variables.TryGetValue(key, out var value)
                    && value == "https://round-trip")
                {
                    await context.LogAsync("info", $"consume saw {key}").ConfigureAwait(false);
                    return true;
                }
                await context.LogAsync("error",
                    $"consume did NOT see {key} — the sub-plan arrived without the merged output")
                    .ConfigureAwait(false);
                return false;

            case "block":
                // Holds the wave open until cancelled (disconnect-seam test).
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return true;

            default:
                await context.LogAsync("error", $"unknown mode '{mode}'").ConfigureAwait(false);
                return false;
        }
    }

    private static string SetVariableMarker(string name, string value)
    {
        var n = Convert.ToBase64String(Encoding.UTF8.GetBytes(name));
        var v = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return $"##octopus[setVariable name='{n}' value='{v}']";
    }
}

/// <summary>
/// Hosts the REAL <see cref="AgentHub"/> on loopback Kestrel (production
/// AgentJwt validation, SignalR over WebSocket) plus a REAL
/// <see cref="DeploymentWorker"/> sharing the hub's registries, and connects
/// REAL agents (<see cref="SignalRServerLink"/> + <see cref="DeploymentExecutor"/>
/// with this test assembly staged as the step package).
/// </summary>
internal sealed class RoundTripHost : IAsyncDisposable
{
    private const string JwtSigningKey = "roundtrip-test-signing-key-0123456789AB"; // ≥32 bytes

    private readonly WebApplication _app;
    private readonly DeploymentWorker _worker;
    private readonly List<RealAgent> _agents = [];
    private readonly string _agentDataPath;
    private readonly string _serverUrl;

    public sealed record RealAgent(
        SignalRServerLink Link, DeploymentExecutor Executor, MachineExecutionGate Gate)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Link.DisposeAsync().ConfigureAwait(false);
            // F2: the machine execution slot moved out of the executor into this
            // shared singleton, so teardown disposes the gate instead.
            Gate.Dispose();
        }
    }

    private RoundTripHost(
        WebApplication app, DeploymentWorker worker, string agentDataPath, string serverUrl)
    {
        _app = app;
        _worker = worker;
        _agentDataPath = agentDataPath;
        _serverUrl = serverUrl;
    }

    public static async Task<RoundTripHost> StartAsync(
        PostgresFixture postgres, EngineOptions? engineOptions = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:JwtSigningKey"] = JwtSigningKey,
        });

        var services = builder.Services;
        services.AddKrakenDeployData(postgres.ConnectionString);
        services.AddSingleton<Core.Domain.Variables.IEncryptionService>(
            _ => TestCrypto.Service(Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddSingleton<IAgentConnectionRegistry, InMemoryAgentConnectionRegistry>();
        services.AddSingleton<IPendingSubPlanRegistry, PendingSubPlanRegistry>();
        services.AddSingleton<IPendingAdhocRegistry, PendingAdhocRegistry>();
        services.AddSingleton<ServerScriptStepRunner>();
        services.AddSingleton<DeployReleaseStepRunner>();
        services.AddSingleton<IHubContext<UiHub, IUiHubClient>>(new NullUiHubContext());
        services.AddSingleton<TargetStatusPublisher>();
        services.AddSingleton<ITargetStatusNotifier, InMemoryTargetStatusNotifier>();
        services.AddSingleton<Core.Domain.Accounts.IAccountContext, Accounts.DisabledAccountContext>();
        services.AddSingleton<AgentJwtService>();
        // B6 — the REAL cancel pusher over the REAL hub, so an operator cancel travels
        // the actual wire to the agent instead of a test shortcut.
        services.AddSingleton<IAgentCancelPusher, AgentCancelPusher>();

        // Mirrors Program.cs's AgentJwt scheme: same issuer/audience, the
        // query-string token hand-off SignalR WebSockets require, and the A8
        // OnTokenValidated atv-revocation check.
        services.AddAuthentication()
            .AddJwtBearer("AgentJwt", options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(JwtSigningKey)),
                    ValidIssuer = AgentJwtService.Issuer,
                    ValidAudience = AgentJwtService.Audience,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                };
                options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token))
                        {
                            context.Token = token;
                        }
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        var dbFactory = context.HttpContext.RequestServices
                            .GetRequiredService<IDbContextFactory<KrakenDbContext>>();
                        var outcome = await AgentTokenValidator
                            .ValidateAsync(
                                context.Principal, dbFactory, context.HttpContext.RequestAborted)
                            .ConfigureAwait(false);
                        if (outcome != AgentTokenValidator.Outcome.Valid)
                        {
                            context.Fail("Agent token is no longer valid.");
                        }
                    },
                };
            });
        services.AddAuthorization();
        services.AddSignalR();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub<AgentHub>("/hubs/agent");
        await app.StartAsync().ConfigureAwait(false);

        var serverUrl = app.Services
            .GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        var worker = new DeploymentWorker(
            queue:                Channel.CreateUnbounded<TenantWorkItem>(),
            registry:             app.Services.GetRequiredService<IAgentConnectionRegistry>(),
            agentHub:             app.Services.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>(),
            serverRunner:         app.Services.GetRequiredService<ServerScriptStepRunner>(),
            deployReleaseRunner:  app.Services.GetRequiredService<DeployReleaseStepRunner>(),
            offlineBundleBuilder: new OfflineDropBundleBuilder(
                                      NullLogger<OfflineDropBundleBuilder>.Instance),
            subPlans:             app.Services.GetRequiredService<IPendingSubPlanRegistry>(),
            scopeFactory:         app.Services.GetRequiredService<IServiceScopeFactory>(),
            diagnosisChannel:     new DeploymentDiagnosisChannel(),
            inFlightGauge:        new InFlightWorkGauge(),
            timeProvider:         TimeProvider.System,
            engineOptions:        Options.Create(engineOptions ?? new EngineOptions()),
            logger:               NullLogger<DeploymentWorker>.Instance);

        var agentDataPath = Path.Combine(
            Path.GetTempPath(), $"kraken-roundtrip-agent-{Guid.NewGuid():N}");
        StageRoundTripStepPackage(agentDataPath);

        return new RoundTripHost(app, worker, agentDataPath, serverUrl);
    }

    /// <summary>Connects a REAL agent (WebSocket SignalR + real executor with the
    /// staged step package) for <paramref name="target"/>, minting a production
    /// JWT so the full AgentJwt validation chain runs.</summary>
    public async Task<RealAgent> ConnectRealAgentAsync(DeploymentTarget target)
    {
        var loaderConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"]                        = _agentDataPath,
                ["StepPackages:AllowUnsignedLoads"] = "true",
            })
            .Build();
        var loader = new StepPackageLoader(loaderConfig, NullLogger<StepPackageLoader>.Instance);

        var link = new SignalRServerLink(NullLogger<SignalRServerLink>.Instance);
        var executionGate = new MachineExecutionGate();
        var executor = new DeploymentExecutor(
            link,
            new NeverUsedPackageSource(),
            new NullArtifactSink(),
            loader,
            executionGate,
            // DataPath MUST match the loader's + the staged package dir: the executor
            // stages each step's working dirs under it, and the production default
            // (/var/lib/krakendeploy-agent on Linux) is NOT writable by a non-root CI
            // runner — leaving it unset made every agent-side step throw
            // UnauthorizedAccessException on ubuntu-latest while passing on Windows
            // (path resolves onto C:\) and in a root container.
            Options.Create(new AgentConfig { DataPath = _agentDataPath }),
            Options.Create(new AgentUpdateConfig()),
            NullLogger<DeploymentExecutor>.Instance);

        // Mirrors ServerLinkHostedService's wiring EXACTLY, including the thing that
        // matters: the handler returns Task.CompletedTask instead of the work task.
        // Returning the work task (what `Task.Run(() => …)` yields — it unwraps) makes
        // the SignalR client await it before dispatching the next push, which is the
        // F2-followup-1 defect: two deployments could never overlap and a cancel push
        // queued behind the deployment it targeted. Tests here would silently pass
        // against the broken shape if this harness kept it.
        link.OnRunDeployment(plan =>
        {
            _ = Task.Run(() => executor.ExecuteAsync(plan));
            return Task.CompletedTask;
        });
        link.OnCancelDeployment((taskId, reason) =>
        {
            executor.TryCancel(taskId, reason);
            return Task.CompletedTask;
        });

        var token = _app.Services.GetRequiredService<AgentJwtService>()
            .Issue(target.Id, target.AgentTokenVersion);
        await link.StartAsync(_serverUrl, () => token, releaseId: null, CancellationToken.None)
            .ConfigureAwait(false);

        // Exercise the real B6 registration leg — must be accepted. This has to come
        // BEFORE waiting on dispatch eligibility: F5 made the two states distinct, so
        // GetConnectionId stays null until RegisterAsync has passed (a connection whose
        // wire-contract version is unverified must never be handed work). Waiting first
        // and registering second — the pre-F5 order — can now never converge.
        // SignalR processes client invocations only after OnConnectedAsync returns, so
        // the connection is already tracked by the time this lands.
        var registration = await link.RegisterAsync(
            new AgentRegistrationRequest(
                target.Id, "roundtrip-machine", "TestOS", "0.0-test", 0L, 0L,
                AgentContract.CurrentVersion),
            CancellationToken.None).ConfigureAwait(false);
        if (registration is not { Accepted: true })
        {
            throw new InvalidOperationException(
                $"Real registration refused: {registration?.Message ?? "null result"}");
        }

        // Now the connection must be DISPATCHABLE. Still polled rather than assumed:
        // the hub marks eligibility at the end of RegisterAsync, and the client's
        // completion can observe the RPC's return before that write is visible here.
        var registry = _app.Services.GetRequiredService<IAgentConnectionRegistry>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (registry.GetConnectionId(target.Id) is null)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    "agent connection never became dispatchable after a successful " +
                    "RegisterAsync (F5: eligibility requires MarkRegistered)");
            }
            await Task.Delay(25).ConfigureAwait(false);
        }

        var agent = new RealAgent(link, executor, executionGate);
        _agents.Add(agent);
        return agent;
    }

    public Task RunDeploymentAsync(Guid deploymentId, CancellationToken ct = default)
        => _worker.DispatchForTestAsync(deploymentId, ct);

    /// <summary>
    /// Cancels through the REAL <see cref="DeploymentService"/> in THIS host's
    /// container, so the B6 cooperative-cancel push travels the real hub to the real
    /// agent. Using the seeder harness's CancelAsync instead would fire its own
    /// pusher over the FAKE hub and never reach the agent.
    /// </summary>
    public async Task CancelDeploymentAsync(Guid deploymentId, CancellationToken ct = default)
    {
        await using var scope = _app.Services
            .GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<DeploymentService>();
        await svc.CancelAsync(
                deploymentId, Core.Domain.Security.CallerAuthorization.System, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Stages THIS test assembly as the round-trip step package in the
    /// agent's package cache (the SamplePluginStepHandler loader pattern).</summary>
    private static void StageRoundTripStepPackage(string dataPath)
    {
        var dir = Path.Combine(
            dataPath, "step-packages-cache",
            RoundTripSteps.PackageId, RoundTripSteps.PackageVersion);
        Directory.CreateDirectory(dir);

        var manifest = new StepPackageManifest
        {
            Id               = RoundTripSteps.PackageId,
            Version          = RoundTripSteps.PackageVersion,
            DisplayName      = "Round-trip test handler",
            TargetFramework  = "net10.0",
            StepTypes        = [RoundTripSteps.StepType],
            ExecutorAssembly = typeof(RoundTripStepHandler).Assembly.GetName().Name + ".dll",
            ExecutorTypeName = typeof(RoundTripStepHandler).FullName!,
            Signature        = "unsigned-dev-build",
            SignedBy         = "kraken-project",
        };
        File.WriteAllText(
            Path.Combine(dir, StepPackageFiles.ManifestFileName),
            StepPackageManifestJson.Serialize(manifest));

        var executorDir = Path.Combine(dir, StepPackageFiles.ExecutorDirectory);
        Directory.CreateDirectory(executorDir);
        var assemblyPath = typeof(RoundTripStepHandler).Assembly.Location;
        File.Copy(
            assemblyPath,
            Path.Combine(executorDir, Path.GetFileName(assemblyPath)),
            overwrite: true);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var agent in _agents)
        {
            try { await agent.DisposeAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            try { await _app.StopAsync(stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        await _app.DisposeAsync().ConfigureAwait(false);
        try { Directory.Delete(_agentDataPath, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class NeverUsedPackageSource : IPackageSource
    {
        public Task<string> DownloadAsync(
            string packageId, string version, string destDirectory, CancellationToken ct)
            => throw new NotSupportedException("round-trip steps carry no package");
    }

    private sealed class NullArtifactSink : IArtifactSink
    {
        public Task<string?> UploadAsync(
            Guid deploymentId, string stepName, string filePath, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }
}
