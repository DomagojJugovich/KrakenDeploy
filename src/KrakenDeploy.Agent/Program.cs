using System.Globalization;
using KrakenDeploy.Agent;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Deployment;
using KrakenDeploy.Agent.Identity;
using KrakenDeploy.Agent.Machine;
using KrakenDeploy.Agent.Services;
using KrakenDeploy.Agent.Transport;
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
    builder.Services.AddSingleton<IServerLink, SignalRServerLink>();
    builder.Services.AddSingleton<GrpcPackageDownloader>();

    // ── Scoped/Transient services ────────────────────────────────────────
    builder.Services.AddTransient<ScriptRunner>();
    builder.Services.AddTransient<DeploymentExecutor>();

    // ── Hosted services — registered in start-up order ───────────────────
    // 1. RegistrationHostedService populates AgentContext.
    // 2. ServerLinkHostedService awaits AgentContext then opens the hub connection.
    // 3. HeartbeatHostedService awaits AgentContext then begins the 30-s tick.
    builder.Services.AddHostedService<RegistrationHostedService>();
    builder.Services.AddHostedService<ServerLinkHostedService>();
    builder.Services.AddHostedService<HeartbeatHostedService>();

    var host = builder.Build();
    await host.RunAsync();
    return 0;
}
