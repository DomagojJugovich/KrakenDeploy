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
    // ── Offline drop runner mode ─────────────────────────────────────────────
    // `KrakenDeploy.Agent --run-offline-drop <bundleDir> [--key <b64> | --key-file <path>]`
    // Executes a single bundle locally (no registration / hub) through the same
    // DeploymentExecutor, then exits. The per-target bundle key is delivered
    // out-of-band; read it from an inline arg, a file, or KRAKEN_BUNDLE_KEY.
    var offlineIdx = Array.IndexOf(args, "--run-offline-drop");
    if (offlineIdx >= 0)
    {
        return await RunOfflineDropAsync(args, offlineIdx);
    }

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

    // SignalR (Reverse mode) is the only live-agent transport: the agent opens a
    // persistent outbound connection to the server, and the server pushes work back
    // down that same full-duplex connection (no inbound port on the agent). Air-
    // gapped targets use OfflineDrop, which runs server-side with no agent link.
    builder.Services.AddSingleton<IServerLink>(
        sp => sp.GetRequiredService<SignalRServerLink>());

    // Package cache — stored under {dataPath}/package-cache/{packageId}/{version}/
    builder.Services.AddSingleton<IPackageCache>(sp =>
    {
        var config    = sp.GetRequiredService<IOptions<AgentConfig>>().Value;
        var cacheRoot = Path.Combine(config.ResolvedDataPath, "package-cache");
        return new LocalPackageCache(cacheRoot);
    });

    // Online package/artifact ports. Like GrpcStepPackageDownloader, they
    // close over AgentContext via accessor delegates so they resolve the
    // server URL + token at call time (after registration completes). The
    // executor depends on the IPackageSource / IArtifactSink ports so the
    // offline runner can swap in bundle-backed implementations.
    builder.Services.AddSingleton<GrpcPackageDownloader>(sp =>
    {
        var ctx = sp.GetRequiredService<AgentContext>();
        return new GrpcPackageDownloader(
            sp.GetRequiredService<IPackageCache>(),
            () => GrpcBaseUrl(sp) ?? ctx.Identity?.ServerUrl  ?? throw new InvalidOperationException("Agent identity not yet ready."),
            () => ctx.Identity?.AgentToken ?? throw new InvalidOperationException("Agent identity not yet ready."),
            sp.GetRequiredService<IOptions<ServerOptions>>().Value.AllowInsecureHttp,
            sp.GetRequiredService<ILogger<GrpcPackageDownloader>>());
    });
    builder.Services.AddSingleton<IPackageSource>(sp => sp.GetRequiredService<GrpcPackageDownloader>());

    builder.Services.AddSingleton<GrpcArtifactUploader>(sp =>
    {
        var ctx = sp.GetRequiredService<AgentContext>();
        return new GrpcArtifactUploader(
            () => GrpcBaseUrl(sp) ?? ctx.Identity?.ServerUrl  ?? throw new InvalidOperationException("Agent identity not yet ready."),
            () => ctx.Identity?.AgentToken ?? throw new InvalidOperationException("Agent identity not yet ready."),
            sp.GetRequiredService<IOptions<ServerOptions>>().Value.AllowInsecureHttp,
            sp.GetRequiredService<ILogger<GrpcArtifactUploader>>());
    });
    builder.Services.AddSingleton<IArtifactSink>(sp => sp.GetRequiredService<GrpcArtifactUploader>());

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
            serverUrl:  () => GrpcBaseUrl(sp) ?? ctx.Identity?.ServerUrl  ?? throw new InvalidOperationException("Agent identity not yet ready."),
            agentToken: () => ctx.Identity?.AgentToken ?? throw new InvalidOperationException("Agent identity not yet ready."),
            extract:    (name, version, archivePath) =>
            {
                sp.GetRequiredService<StepPackageLoader>()
                  .ExtractToCache(name, version, archivePath);
                return Task.CompletedTask;
            },
            logger: log,
            allowInsecureHttp: sp.GetRequiredService<IOptions<ServerOptions>>().Value.AllowInsecureHttp);
    });

    // ── Step handlers ───────────────────────────────────────────────────
    // Every step handler ships as a step package (Phase D-8). The agent's
    // StepPackageLoader pulls them from the server; DeploymentExecutor
    // instantiates the handler types via Activator from the package's
    // collectible ALC. No in-DI handlers, no fallback path — see
    // docs/architecture.md "Pre-production policy" and TASKS.md D-8.9.

    // ── Scoped/Transient services ────────────────────────────────────────
    // B7/F2: the machine execution gate — this box's single execution slot —
    // MUST be a process-wide singleton, and is now shared by the deployment and
    // ad-hoc paths (F2 brought ad-hoc scripts under it). A non-singleton
    // registration would hand each consumer its own semaphore and silently
    // disable serialization altogether.
    builder.Services.AddSingleton<MachineExecutionGate>();
    // E5: DeploymentExecutor is a process-wide SINGLETON — it holds the in-flight
    // registry (_running) that AgentUpdateService.IsExecuting reads to refuse a
    // binary swap mid-deployment. Registered Transient, ServerLinkHostedService
    // (runs deployments) and AgentUpdateService (reads the guard) each got their
    // OWN instance, so the updater's guard read a permanently-empty map and
    // could swap binaries mid-deployment. All ctor deps are singletons, so a
    // singleton lifetime is captive-dependency-safe.
    builder.Services.AddSingleton<DeploymentExecutor>();
    // M11.E.7 — fail-closed verify-then-run handler for adhoc agent actions.
    builder.Services.AddSingleton<KrakenDeploy.Agent.Adhoc.IAdhocScriptInvoker,
        KrakenDeploy.Agent.Adhoc.ScriptRunnerInvoker>();
    builder.Services.AddTransient<KrakenDeploy.Agent.Adhoc.AdhocScriptExecutor>();

    // ── Hosted services — registered in start-up order ───────────────────
    // 1. RegistrationHostedService populates AgentContext.
    // 2. ServerLinkHostedService awaits AgentContext then opens the hub connection.
    // 3. HeartbeatHostedService awaits AgentContext then begins the 30-s tick.
    // 4. TokenRefreshHostedService awaits AgentContext then renews the bearer
    //    token at half-life (A8 sliding refresh).
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddHostedService<RegistrationHostedService>();
    builder.Services.AddHostedService<ServerLinkHostedService>();
    builder.Services.AddHostedService<HeartbeatHostedService>();
    builder.Services.AddHostedService<AgentUpdateService>();
    builder.Services.AddHostedService<TokenRefreshHostedService>();

    var host = builder.Build();
    await host.RunAsync();
    return 0;
}

static async Task<int> RunOfflineDropAsync(string[] args, int flagIndex)
{
    if (flagIndex + 1 >= args.Length)
    {
        Log.Fatal("--run-offline-drop requires a bundle directory path.");
        return 2;
    }
    var bundleDir = args[flagIndex + 1];

    // Precedence: --key, then --key-file, then KRAKEN_BUNDLE_KEY. A --key-file
    // that was passed but doesn't exist is an explicit error — don't silently
    // fall through to the env var (which would mask the typo as 'wrong key').
    var keyFile = GetArgValue(args, "--key-file");
    var keyB64 = GetArgValue(args, "--key");
    if (keyB64 is null && keyFile is not null)
    {
        if (!File.Exists(keyFile))
        {
            Log.Fatal("--key-file '{Path}' does not exist.", keyFile);
            return 2;
        }
        keyB64 = (await File.ReadAllTextAsync(keyFile)).Trim();
    }
    keyB64 ??= Environment.GetEnvironmentVariable("KRAKEN_BUNDLE_KEY");

    if (string.IsNullOrWhiteSpace(keyB64))
    {
        Log.Fatal(
            "No bundle key supplied. Pass --key <base64>, --key-file <path>, " +
            "or set KRAKEN_BUNDLE_KEY.");
        return 2;
    }

    byte[] key;
    try
    {
        key = Convert.FromBase64String(keyB64.Trim());
    }
    catch (FormatException)
    {
        Log.Fatal("Bundle key is not valid base64.");
        return 2;
    }

    using var loggerFactory = LoggerFactory.Create(lb => lb.AddSerilog());
    var runner = new KrakenDeploy.Agent.Offline.OfflineRunner(loggerFactory);
    return await runner.RunAsync(bundleDir, key, CancellationToken.None);
}

static string? GetArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// B8 — the gRPC channels' base URL: Server:GrpcUrl when configured (a
// cleartext deployment's dedicated Http2-only h2c endpoint — one plaintext
// port cannot serve HTTP/1.1 and HTTP/2 without ALPN), null otherwise so the
// accessors fall back to the identity's server URL.
static string? GrpcBaseUrl(IServiceProvider sp)
{
    var url = sp.GetRequiredService<IOptions<ServerOptions>>().Value.GrpcUrl;
    return string.IsNullOrWhiteSpace(url) ? null : url;
}
