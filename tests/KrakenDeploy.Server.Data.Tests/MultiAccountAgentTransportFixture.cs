using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Spaces;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// What a test agent presents in the wire-contract handshake header: the current version,
/// a specific (skewed) version, or nothing at all. "Absent" is a distinct path in
/// <c>AgentContractHandshakeGate</c> — an agent predating the header — and must not be
/// simulated by sending a zero.
/// </summary>
public sealed record PresentedContract(int? Value)
{
    public static PresentedContract Current { get; } = new(AgentContract.CurrentVersion);

    public static PresentedContract Absent { get; } = new((int?)null);

    public static PresentedContract Version(int version) => new(version);
}

/// <summary>
/// Spins up one PostgreSQL container and provisions TWO tenant databases
/// (<c>kraken_acct_alpha</c>, <c>kraken_acct_beta</c>) — one per simulated SaaS
/// business account — each migrated to the current schema (the Default Space is
/// migration-seeded). Builds an in-memory <see cref="TestServer"/> host that
/// wires the production multi-account data registration plus the real agent
/// transport (filter + hub + auth), and hands out real <see cref="HubConnection"/>
/// clients keyed to each account's subdomain. See
/// <see cref="MultiAccountAgentTransportE2ETests"/> for the fidelity boundary.
/// </summary>
public sealed class MultiAccountAgentTransportFixture : IAsyncLifetime
{
    // HS256 needs >= 32 bytes. Test-only key; never a real secret.
    private readonly string _signingKey = "kraken-e2e-agent-jwt-signing-key-0123456789";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("kraken_bootstrap")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true)
        .Build();

    private string _dataPath = "";

    public AccountInfo Alpha { get; private set; } = null!;
    public AccountInfo Beta { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _dataPath = Path.Combine(Path.GetTempPath(), "kraken-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataPath);

        Alpha = await CreateAccountAsync("alpha");
        Beta = await CreateAccountAsync("beta");
    }

    private async Task<AccountInfo> CreateAccountAsync(string subdomain)
    {
        var dbName = $"kraken_acct_{subdomain}";
        var baseConn = _container.GetConnectionString();

        await using (var admin = new NpgsqlConnection(baseConn))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\";";
            await cmd.ExecuteNonQueryAsync();
        }

        var conn = new NpgsqlConnectionStringBuilder(baseConn) { Database = dbName }.ConnectionString;
        var account = new AccountInfo(Guid.NewGuid(), subdomain, conn);
        await account.MigrateAsync();
        return account;
    }

    /// <summary>
    /// Builds + starts an in-memory host running the real agent transport pipeline.
    /// Each call yields a fresh host (fresh registry + TestServer) so tests don't
    /// share connection state.
    /// </summary>
    public async Task<WebApplication> BuildHostAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Production data-layer registration in multi-account mode — the real
        // account-routing DbContext factory + interceptors. The fallback connection
        // is never used on the hub path (OnConfiguring overrides it per account).
        builder.Services.AddKrakenDeployData(Alpha.ConnectionString, _dataPath, multiAccount: true);

        // Replace the placeholder account services (AddKrakenDeployData TryAdd'd the
        // disabled / null variants; an explicit Add wins resolution).
        builder.Services.AddScoped<IAccountContext, AsyncLocalAccountContext>();
        builder.Services.AddSingleton<IAccountResolver>(new StubAccountResolver(
            new Dictionary<string, ResolvedAccount>(StringComparer.OrdinalIgnoreCase)
            {
                [Alpha.Host] = Alpha.ToResolvedAccount(),
                [Beta.Host] = Beta.ToResolvedAccount(),
            }));

        // AgentHub depends on IEncryptionService (T0-6: it encrypts sensitive
        // output variables). Production registers it via AddKrakenDeployEncryption;
        // this host needs it so the hub can be activated on connect. A fixed-key
        // test service suffices — these tests never report output variables, so
        // Encrypt/Decrypt (and thus the DEK) is never touched.
        builder.Services.AddSingleton<KrakenDeploy.Server.Core.Domain.Variables.IEncryptionService>(
            TestCrypto.Service("S3Jha2VuRGVwbG95RGV2TWFzdGVyS2V5MzJCeXRlcyE="));

        // Agent transport services (mirrors KrakenDeploy.Server/Program.cs).
        builder.Services.AddSingleton<IAgentConnectionRegistry, InMemoryAgentConnectionRegistry>();
        builder.Services.AddSingleton<ITargetStatusNotifier, InMemoryTargetStatusNotifier>();
        builder.Services.AddSingleton<TargetStatusPublisher>();
        // The gate resolves this when it refuses a handshake. Registered even though this
        // fixture has no AccountResolutionMiddleware (so the write itself cannot succeed) —
        // the gate's contract is that a recording failure must not change the 426, and
        // omitting the registration would test a DI error instead.
        builder.Services.AddScoped<IAgentContractRefusalRecorder, AgentContractRefusalRecorder>();
        builder.Services.AddSingleton<IPendingSubPlanRegistry, PendingSubPlanRegistry>();
        builder.Services.AddSingleton<IPendingAdhocRegistry, PendingAdhocRegistry>();
        builder.Services.AddSingleton<AgentAccountHubFilter>();

        var signalR = builder.Services.AddSignalR();
        signalR.AddHubOptions<AgentHub>(o => o.AddFilter<AgentAccountHubFilter>());

        // AgentJwt bearer scheme (mirrors Program.cs: HS256, iss/aud stamped-not-enforced,
        // query-string token fallback for the hub path).
        var keyBytes = Encoding.UTF8.GetBytes(_signingKey);
        builder.Services.AddAuthentication("AgentJwt")
            .AddJwtBearer("AgentJwt", options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(2),
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var token = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) &&
                            ctx.HttpContext.Request.Path.StartsWithSegments("/hubs/agent"))
                        {
                            ctx.Token = token;
                        }
                        return Task.CompletedTask;
                    },
                };
            });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        // Mirrors the production pipeline order AND its wiring: the gate sits after
        // authentication (so its audit row can name the target) and is scoped by the
        // RequiresAgentContract metadata on the hub endpoint, not by a path string. The
        // marker below is load-bearing — drop it and the gate never fires, which is exactly
        // the fail-open mode a path-matched gate had whenever a route drifted.
        app.UseAgentContractGate();
        app.MapHub<AgentHub>("/hubs/agent").WithMetadata(new RequiresAgentContract());
        await app.StartAsync();
        return app;
    }

    /// <summary>
    /// A real SignalR client connecting to the in-memory server with the given
    /// account's <c>Host</c> header (or <paramref name="hostOverride"/>) and a JWT
    /// authenticating as <paramref name="tokenTargetId"/>. LongPolling is forced so
    /// the connection routes through the TestServer's in-memory handler.
    /// </summary>
    public HubConnection BuildConnection(
        WebApplication host,
        AccountInfo account,
        Guid tokenTargetId,
        string? hostOverride = null,
        PresentedContract? contract = null)
    {
        var server = (TestServer)host.Services.GetRequiredService<IServer>();
        var requestHost = hostOverride ?? account.Host;
        var token = MintToken(tokenTargetId);

        return new HubConnectionBuilder()
            .WithUrl($"http://{requestHost}/hubs/agent", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);

                // The handshake contract gate refuses a connection whose declared wire
                // version is absent or wrong, so a fixture standing in for a real agent must
                // declare it exactly as SignalRServerLink does. One tri-state knob rather
                // than two independent ones: "which version" and "any version at all" are
                // not orthogonal, and expressing them separately left a fourth combination
                // (a version AND omit it) that means nothing.
                if ((contract ?? PresentedContract.Current).Value is { } declared)
                {
                    options.Headers[AgentContract.VersionHeader] =
                        declared.ToString(CultureInfo.InvariantCulture);
                }
            })
            .Build();
    }

    // Mirrors KrakenDeploy.Server.Services.AgentJwtService.Issue: HS256, NameIdentifier
    // = targetId (the value AgentHub.GetTargetId reads back).
    private string MintToken(Guid targetId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_signingKey));
        var handler = new JwtSecurityTokenHandler();
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, targetId.ToString())]),
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(30),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        try
        {
            if (Directory.Exists(_dataPath))
            {
                Directory.Delete(_dataPath, recursive: true);
            }
        }
        catch
        {
            // Best effort — temp dir cleanup must not fail the test run.
        }
    }
}

/// <summary>
/// Per-account routing facts plus the per-account DB helpers (each operates on this
/// account's tenant database via <see cref="ConnectionString"/>).
/// </summary>
public sealed record AccountInfo(Guid AccountId, string Subdomain, string ConnectionString)
{
    public string Host => $"{Subdomain}.kraken.test";

    public ResolvedAccount ToResolvedAccount() =>
        new(AccountId, Subdomain, $"secret://{Subdomain}", ConnectionString);

    public async Task MigrateAsync()
    {
        await using var db = NewContext(ConnectionString);
        await db.Database.MigrateAsync();
    }

    /// <summary>Inserts a fresh deployment target (Offline by default) into this tenant DB.</summary>
    public async Task<Guid> SeedTargetAsync(TargetStatus status = TargetStatus.Offline)
    {
        var id = Guid.NewGuid();
        await using var db = NewContext(ConnectionString);
        db.DeploymentTargets.Add(new DeploymentTarget
        {
            Id = id,
            SpaceId = WellKnown.DefaultSpaceId,
            Name = $"{Subdomain}-{id:N}"[..20],
            Roles = ["web"],
            TransportMode = TransportMode.Reverse,
            Status = status,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>A bare context on this account's tenant DB, for assertions.</summary>
    public KrakenDbContext OpenContext() => NewContext(ConnectionString);

    private static KrakenDbContext NewContext(string conn)
    {
        var options = new DbContextOptionsBuilder<KrakenDbContext>()
            .UseNpgsql(conn)
            .UseSnakeCaseNamingConvention()
            // Lambda value converters can't round-trip through the snapshot; the runtime
            // comparison flags a false diff. Same suppression PostgresFixture uses.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new KrakenDbContext(options, new DefaultSpaceContext());
    }
}

/// <summary>
/// Multi-account <see cref="IAccountContext"/> test double. Mirrors the production
/// <c>HttpAccountContext</c> contract the agent path actually uses: <c>WithAccount</c>
/// pushes an ambient override that flows across awaits + child DI scopes (static
/// <see cref="AsyncLocal{T}"/>), and <c>ResolveTenantConnectionString</c> returns the
/// active account's connection or throws (fail closed). The production type lives in
/// the un-referenced <c>KrakenDeploy.Server</c> app project, hence the local double.
/// </summary>
file sealed class AsyncLocalAccountContext : IAccountContext
{
    private static readonly AsyncLocal<ResolvedAccount?> Override = new();
    private ResolvedAccount? _resolved;

    private ResolvedAccount? Current => Override.Value ?? _resolved;

    public Guid CurrentAccountId => Require().Id;
    public string Subdomain => Require().Subdomain;
    public string ConnectionStringRef => Require().ConnectionStringRef;
    public string ConnectionString => Require().ConnectionString;
    public bool IsResolved => Current is not null;

    public string? ResolveTenantConnectionString() => Require().ConnectionString;

    public void SetResolved(ResolvedAccount account) => _resolved = account;

    public IDisposable WithAccount(ResolvedAccount account)
    {
        var previous = Override.Value;
        Override.Value = account;
        return new Pop(previous);
    }

    private ResolvedAccount Require() => Current
        ?? throw new InvalidOperationException(
            "No business account resolved for this scope (fail closed).");

    private sealed class Pop(ResolvedAccount? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}

/// <summary>
/// Host→account resolver stub. Stands in for <c>CatalogAccountResolver</c>: the E2E
/// concern is "given host→account, does the agent path isolate", not catalog lookup.
/// </summary>
file sealed class StubAccountResolver(IReadOnlyDictionary<string, ResolvedAccount> byHost) : IAccountResolver
{
    public Task<ResolvedAccount?> ResolveAsync(string host, CancellationToken ct = default)
        => Task.FromResult(byHost.TryGetValue(host, out var account) ? account : null);

    public Task<ResolvedAccount?> ResolveByIdAsync(Guid accountId, CancellationToken ct = default)
        => Task.FromResult<ResolvedAccount?>(byHost.Values.FirstOrDefault(a => a.Id == accountId));
}
