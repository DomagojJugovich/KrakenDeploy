using System.Globalization;
using KrakenDeploy.Agent;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment;
using KrakenDeploy.Agent.Identity;
using KrakenDeploy.Agent.Machine;
using KrakenDeploy.Agent.Services;
using KrakenDeploy.Agent.StepPackages;
using KrakenDeploy.Agent.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

// Bootstrap logger — active until the full Serilog pipeline is wired in.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
        formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    return await RunAsync(args);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Agent terminated unexpectedly.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static async Task<int> RunAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // ── Options ─────────────────────────────────────────────────────────
    builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));
    builder.Services.Configure<AgentConfig>(builder.Configuration.GetSection("Agent"));
    builder.Services.Configure<AgentUpdateConfig>(
        builder.Configuration.GetSection("Agent:Update"));

    // ── Serilog ─────────────────────────────────────────────────────────
    // Resolve the data path early so the rolling log file goes to the right place.
    var dataPath = builder.Configuration["Agent:DataPath"];
    if (string.IsNullOrWhiteSpace(dataPath))
    {
        dataPath = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "KrakenDeploy", "Agent")
            : "/var/lib/krakendeploy-agent";
    }

    var logFilePath = Path.Combine(dataPath, "logs", "agent-.log");

    builder.Services.AddSerilog(lc => lc
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
            formatProvider: CultureInfo.InvariantCulture)
        .WriteTo.File(
            logFilePath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
            formatProvider: CultureInfo.InvariantCulture));

    // ── Singletons ───────────────────────────────────────────────────────
    builder.Services.AddSingleton<AgentContext>();
    builder.Services.AddSingleton<AgentIdentityStore>();
    builder.Services.AddSingleton<MachineInfoCollector>();
    builder.Services.AddSingleton<SignalRServerLink>();
    builder.Services.AddSingleton<DirectServerLink>();
    builder.Services.AddSingleton<PollingServerLink>();

    // Select the active IServerLink based on the transport mode returned by the
    // server during registration.  SignalR is the default (Reverse mode).
    builder.Services.AddSingleton<IServerLink>(sp =>
    {
        var ctx = sp.GetRequiredService<AgentContext>();
        return ctx.TransportMode switch
        {
            "Direct" => sp.GetRequiredService<DirectServerLink>(),
            "Polling" => sp.GetRequiredService<PollingServerLink>(),
            _ => sp.GetRequiredService<SignalRServerLink>(),
        };
    });

    // Package cache — stored under {dataPath}/package-cache/{packageId}/{version}/
    builder.Services.AddSingleton<IPackageCache>(sp =>
    {
        var config    = sp.GetRequiredService<IOptions<AgentConfig>>().Value;
        var cacheRoot = Path.Combine(config.ResolvedDataPath, "package-cache");
        return new LocalPackageCache(cacheRoot);
    });

    builder.Services.AddSingleton<GrpcPackageDownloader>();
    builder.Services.AddSingleton<GrpcArtifactUploader>();

    // ── Step-package loader + gRPC source (Phase D-4 / D-5) ──────────────
    // The downloader is a singleton: it closes over AgentContext via
    // accessor delegates so it resolves the server URL + agent token at
    // call time (after registration completes). The loader takes the
    // downloader through the IStepPackageSource port so tests can swap it.
    builder.Services.AddSingleton<StepPackageLoader>(sp =>
    {
        // The IStepPackageSource is set up below — circular reference is
        // resolved by capturing the IServiceProvider and resolving lazily.
        var cfg    = sp.GetRequiredService<IConfiguration>();
        var log    = sp.GetRequiredService<ILogger<StepPackageLoader>>();
        var source = sp.GetRequiredService<IStepPackageSource>();
        return new StepPackageLoader(cfg, log, source);
    });
    builder.Services.AddSingleton<IStepPackageSource>(sp =>
    {
        var ctx = sp.GetRequiredService<AgentContext>();
        var log = sp.GetRequiredService<ILogger<GrpcStepPackageDownloader>>();

        // Defer the loader lookup to first download — keeps the DI graph
        // acyclic (loader → source → loader is broken by the lazy resolve).
        return new GrpcStepPackageDownloader(
            serverUrl:  () => ctx.Identity?.ServerUrl  ?? throw new InvalidOperationException("Agent identity not yet ready."),
            agentToken: () => ctx.Identity?.AgentToken ?? throw new InvalidOperationException("Agent identity not yet ready."),
            extract:    (name, version, archivePath) =>
            {
                sp.GetRequiredService<StepPackageLoader>()
                  .ExtractToCache(name, version, archivePath);
                return Task.CompletedTask;
            },
            logger: log);
    });

    // ── Step handlers ───────────────────────────────────────────────────
    // Every step handler ships as a step package (Phase D-8). The agent's
    // StepPackageLoader pulls them from the server; DeploymentExecutor
    // instantiates the handler types via Activator from the package's
    // collectible ALC. No in-DI handlers, no fallback path — see
    // docs/architecture.md "Pre-production policy" and TASKS.md D-8.9.

    // ── Scoped/Transient services ────────────────────────────────────────
    builder.Services.AddTransient<DeploymentExecutor>();

    // ── Hosted services — registered in start-up order ───────────────────
    // 1. RegistrationHostedService populates AgentContext.
    // 2. ServerLinkHostedService awaits AgentContext then opens the hub connection.
    // 3. HeartbeatHostedService awaits AgentContext then begins the 30-s tick.
    builder.Services.AddHostedService<RegistrationHostedService>();
    builder.Services.AddHostedService<ServerLinkHostedService>();
    builder.Services.AddHostedService<HeartbeatHostedService>();
    builder.Services.AddHostedService<AgentUpdateService>();

    var host = builder.Build();
    await host.RunAsync();
    return 0;
}
