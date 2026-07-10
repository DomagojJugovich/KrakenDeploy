using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.PostgreSql;
using KrakenDeploy.Ai;
using KrakenDeploy.Contracts;
using KrakenDeploy.Mcp;
using KrakenDeploy.Server.Auth;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Licensing;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Commands;
using KrakenDeploy.Server.Components;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Hangfire;
using KrakenDeploy.Server.Maintenance;
using KrakenDeploy.Server.Services;
using KrakenDeploy.Server.Transport;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.StepTemplates;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Spaces;
using KrakenDeploy.Server.Accounts;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.ControlPlane;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Radzen;
using Serilog;

namespace KrakenDeploy.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // CLI subcommand dispatch — keeps the same executable usable for one-shot
        // admin operations without bringing up the web server.
        if (args.Length > 0)
        {
            // Resolve the content root so CLI commands can find appsettings files
            // regardless of the working directory (dotnet run from repo root, etc.).
            var cliContentRoot = ResolveContentRoot();
            switch (args[0])
            {
                case "users":
                    return await UserCommands.RunAsync(args.AsSpan(1).ToArray(), cliContentRoot).ConfigureAwait(false);
                case "database":
                    return await DatabaseCommands.RunAsync(args.AsSpan(1).ToArray(), cliContentRoot).ConfigureAwait(false);
                case "backup":
                    return await BackupCommands.RunAsync(args.AsSpan(1).ToArray(), cliContentRoot).ConfigureAwait(false);
                case "restore":
                    return await RestoreCommands.RunAsync(args.AsSpan(1).ToArray(), cliContentRoot).ConfigureAwait(false);
                case "seed-demo":
                    return await SeedDemoCommands.RunAsync(args.AsSpan(1).ToArray(), cliContentRoot).ConfigureAwait(false);
                case "apikeys":
                    return await ApiKeyCommands.RunAsync(args.AsSpan(1).ToArray(), cliContentRoot).ConfigureAwait(false);
                case "encryption":
                    return await EncryptionCommands.RunAsync(args.AsSpan(1).ToArray(), cliContentRoot).ConfigureAwait(false);
                case "releases":
                    return await ReleaseCommands.RunAsync(args.AsSpan(1).ToArray(), cliContentRoot).ConfigureAwait(false);
            }
        }

        // Bootstrap logger — active until the full Serilog pipeline is configured
        // via UseSerilog() below.  Writes to stdout only.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture)
            .CreateBootstrapLogger();

        try
        {
            return await RunWebAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Server terminated unexpectedly.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static async Task<int> RunWebAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // ── Serilog ─────────────────────────────────────────────────────────
        // ReadFrom.Configuration picks up the "Serilog" section in appsettings
        // (level overrides, minimum level, etc.).  ReadFrom.Services enables
        // enrichers/sinks that need services from the DI container.
        builder.Host.UseSerilog((context, services, lc) => lc
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .WriteTo.Console(
                outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}" +
                    "{Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                "logs/server-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate:
                    "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] " +
                    "{SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture));

        // ── Data & identity ─────────────────────────────────────────────────
        var connectionString = builder.Configuration.GetConnectionString("KrakenDb")
            ?? throw new InvalidOperationException(
                "Connection string 'KrakenDb' is not configured. " +
                "Set ConnectionStrings:KrakenDb in appsettings.{Environment}.json or via user-secrets.");

        var dataPath = builder.Configuration["Server:DataPath"] ?? "data";

        // SaaS multi-account layer is opt-in (MultiAccount:Enabled). When off, the
        // platform runs single-instance exactly as before: one fixed tenant DB, no
        // subdomain resolution, no control plane.
        var multiAccountEnabled = builder.Configuration.GetValue(
            $"{MultiAccountOptions.SectionName}:{nameof(MultiAccountOptions.Enabled)}", false);

        if (multiAccountEnabled)
        {
            // Tenant connection is resolved per request from the active account
            // (subdomain → catalog → secret); the catalog is its own database.
            var catalogConnectionString = builder.Configuration.GetConnectionString("Catalog")
                ?? throw new InvalidOperationException(
                    "MultiAccount is enabled but connection string 'Catalog' is not configured. " +
                    "Set ConnectionStrings:Catalog.");
            builder.Services.AddKrakenControlPlane(builder.Configuration, catalogConnectionString, dataPath);
            builder.Services.AddKrakenDeployData(connectionString, dataPath, multiAccount: true);
        }
        else
        {
            builder.Services.AddKrakenDeployData(connectionString, dataPath);
        }
        // Registers IKrakenAi + KrakenAiClientFactory + prompt sanitiser/cost
        // catalog. AddKrakenDeployData already registered the DB-backed
        // IKrakenAiSettingsProvider / IKrakenAiCallSink / IBudgetTracker, so
        // AddKrakenAi's TryAdd defaults defer to them; it only fills in the
        // IKrakenAi pieces the AI services (diagnosis, assistant, adhoc) need.
        builder.Services.AddKrakenAi();
        builder.Services.AddKrakenDeployIdentityCore()
            .AddSignInManager();

        // ── Space context (HTTP-aware override of DefaultSpaceContext) ───────
        // Active-Space resolution. The Space rides in the URL (/s/{slug}/…) as a
        // route param: SpaceScopedComponentBase validates it against the user's
        // accessible Spaces and pushes it via SetResolved, on both the prerender
        // and the interactive circuit. Registered as the concrete type (with the
        // interface forwarded to the same instance) so the page base can call
        // SetResolved. The /api surface (no page route) falls back to the Default
        // Space — CLI/agent callers are Default-scoped.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<HttpSpaceContext>();
        builder.Services.AddScoped<ISpaceContext>(sp => sp.GetRequiredService<HttpSpaceContext>());

        // ── Account context (multi-account / SaaS only) ──────────────────────
        // The active account rides in the host subdomain. AccountResolutionMiddleware
        // resolves it and pins it here (fail closed); the account-aware tenant
        // DbContextFactory reads the connection string from it. Mirrors the Space
        // context one level up.
        if (multiAccountEnabled)
        {
            builder.Services.AddScoped<HttpAccountContext>();
            builder.Services.AddScoped<IAccountContext>(
                sp => sp.GetRequiredService<HttpAccountContext>());
            // Per-account scheduled-backup runner (job body + startup reconcile). Only
            // needed in multi-account; injects singleton-safe deps so it carries no
            // captive dependency.
            builder.Services.AddTransient<AccountBackupRunner>();
            // Blue-green §8-6: when this instance's release turns Draining, stop its
            // Hangfire server so new background work runs on the Active release.
            builder.Services.AddHostedService<KrakenDeploy.Server.Hangfire.DrainModeHangfireStopper>();
        }

        // Rate limiting — SCOPED TO /api/agents/register ONLY (applied via
        // .RequireRateLimiting below). Deliberately NOT a global limiter: gov
        // sites NAT many real agents + users behind one public IP, so a global
        // per-IP limiter would throttle legitimate UI / agent traffic. Agent
        // registration is a one-time-per-agent operation whose failure path does
        // NOT consume the registration token, so without a limit the endpoint is
        // a token brute-force oracle. A generous per-IP fixed window crushes
        // brute force while leaving staggered real rollouts room.
        // NOTE: behind a TLS proxy without UseForwardedHeaders, RemoteIpAddress is
        // the proxy's — all callers share one bucket (still fine: registration is
        // infrequent). Sharpen once forwarded headers land (CAT F).
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("agent-register", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window      = TimeSpan.FromMinutes(1),
                        QueueLimit  = 0,
                    }));
        });

        // ── Encryption (AES-256-GCM for sensitive variables) ────────────────
        // In production, set Encryption:MasterKey to a base64-encoded 32-byte key.
        // Generate with: Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        var masterKey = builder.Configuration["Encryption:MasterKey"];
        if (string.IsNullOrWhiteSpace(masterKey))
        {
            if (!builder.Environment.IsDevelopment())
            {
                // Fail fast: an ephemeral key would silently make every sensitive
                // variable encrypted this session permanently undecryptable after
                // the next restart. Refuse to boot a non-Development host without it.
                throw new InvalidOperationException(
                    "Encryption:MasterKey is not configured. Set a base64-encoded 32-byte key " +
                    "(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))) via configuration " +
                    "before starting a non-Development environment.");
            }

            masterKey = Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            Log.Warning(
                "Encryption:MasterKey is not configured — using an ephemeral Development key. " +
                "Sensitive variables encrypted in this session will be unreadable after restart.");
        }

        // Envelope encryption (M13.D.2): masterKey is the KEK; it wraps a
        // DB-resident DEK that actually encrypts data. First-boot DEK generation
        // runs after migrate (dev-boot below; `database setup` for prod).
        //
        // FAIL CLOSED under multi-account: the DEK subsystem is single-instance
        // only. DekProvider is a process-wide singleton caching ONE DEK, so a
        // DB-per-account build would serve the first tenant's DEK to every
        // tenant — cross-customer decrypt failures + silent write corruption —
        // and tenant DBs are never provisioned a DEK anyway. Per-account,
        // account-keyed DEK is deferred; refuse rather than corrupt.
        if (multiAccountEnabled)
        {
            throw new InvalidOperationException(
                "Envelope encryption (M13.D.2) does not yet support MultiAccount:Enabled. The DEK is a " +
                "single process-wide instance, not per-tenant: a shared DekProvider would cache one " +
                "tenant's DEK and serve it to all (cross-customer boundary breach), and provisioned " +
                "tenant DBs have no DEK row. Run single-instance until per-account DEK lands.");
        }
        builder.Services.AddKrakenDeployEncryption(masterKey);

        // ── Data Protection ─────────────────────────────────────────────────
        // Persist the key ring so auth cookies + antiforgery tokens survive
        // restarts and are shared across an HA pair (point DataProtection:KeyPath
        // at a shared volume for HA). Without this, ASP.NET Core uses an ephemeral
        // key ring: every restart logs users out and 400s antiforgery-protected
        // POSTs, and HA nodes can't read each other's cookies/tokens.
        var keyRingPath = builder.Configuration["DataProtection:KeyPath"]
            ?? Path.Combine(builder.Configuration["DataPath"] ?? "data", "dataprotection-keys");
        var dataProtection = builder.Services.AddDataProtection()
            .SetApplicationName("KrakenDeploy")
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
        if (OperatingSystem.IsWindows())
        {
            // Encrypt the key ring at rest with Windows DPAPI (single host). On a
            // Linux host or in HA, protect the shared key directory via volume
            // permissions or configure a certificate-based protector instead.
            dataProtection.ProtectKeysWithDpapi();
        }

        // ── Authentication ───────────────────────────────────────────────────
        builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, options =>
            {
                options.Cookie.Name = "KrakenDeploy.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.LoginPath = "/login";
                options.LogoutPath = "/logout";
                options.AccessDeniedPath = "/login";
                options.ExpireTimeSpan = TimeSpan.FromDays(7);
                options.SlidingExpiration = true;
            });

        // The external-scheme cookie is needed by the OIDC sign-in flow
        // (used as an interim store between the IdP callback and our
        // OnTicketReceived handler that converts it to an application cookie).
        builder.Services.AddAuthentication()
            .AddCookie(IdentityConstants.ExternalScheme);

        // External OIDC SSO. Single-instance: one global scheme per enabled
        // IdentityProvider, registered at startup. Multi-account (SaaS): per-tenant
        // schemes synthesized per request from the resolved account's own DB (see
        // docs/saas-per-account-sso.md). Both no-op gracefully when no providers exist.
        if (multiAccountEnabled)
        {
            OidcRegistrar.RegisterMultiAccountSchemes(builder);
        }
        else
        {
            OidcRegistrar.RegisterSchemes(builder);
        }

        // Agent JWT bearer — separate scheme so it doesn't conflict with the
        // cookie auth used by the Blazor UI.
        var agentJwtKey = builder.Configuration["Agent:JwtSigningKey"];
        if (string.IsNullOrWhiteSpace(agentJwtKey))
        {
            throw new InvalidOperationException(
                "Agent:JwtSigningKey is not configured. " +
                "Set it in appsettings or user-secrets (minimum 32 characters for HS256).");
        }

        var agentJwtKeyBytes = Encoding.UTF8.GetBytes(agentJwtKey);
        if (agentJwtKeyBytes.Length < 32)
        {
            // HS256 needs a >=256-bit (32-byte) key. A shorter key only throws at
            // sign/validate time (IDX10653); fail fast at startup and refuse the
            // weak key that makes agent tokens offline-brute-forceable / forgeable.
            throw new InvalidOperationException(
                "Agent:JwtSigningKey must be at least 32 bytes (256 bits) for HS256. " +
                "Generate one with Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).");
        }

        // Per-user API-key scheme (M13.C.4) — the X-Api-Key header is hashed
        // and resolved against the api_keys table; the principal is the key's
        // OWNER (real NameIdentifier → real RBAC). Missing header → NoResult()
        // so cookie/OIDC auth still chains.
        builder.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, _ => { });

        builder.Services.AddAuthentication()
            .AddJwtBearer("AgentJwt", options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(agentJwtKeyBytes),
                    // Issuer/Audience are stamped on newly issued tokens (see
                    // AgentJwtService) but NOT yet enforced here: already-issued
                    // long-lived tokens carry no iss/aud and would be rejected.
                    // Flip ValidateIssuer/Audience to true after an agent
                    // re-registration window so old tokens have rotated out.
                    ValidIssuer = AgentJwtService.Issuer,
                    ValidAudience = AgentJwtService.Audience,
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ClockSkew = TimeSpan.FromMinutes(2),
                };
                // SignalR WebSocket upgrades cannot carry custom headers,
                // so the token is passed in the query string.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) &&
                            context.HttpContext.Request.Path
                                .StartsWithSegments("/hubs/agent", StringComparison.OrdinalIgnoreCase))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        // ── SignalR & transport ──────────────────────────────────────────────
        var signalR = builder.Services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 1_048_576; // 1 MiB — control plane only
        });

        // P3-8 — agent-transport account identity (multi-account only). The filter
        // resolves the account from the connection's host (host-derived) and pins it
        // for every AgentHub event/invocation, fail-closed. Single-instance installs
        // never add it and run unchanged.
        if (multiAccountEnabled)
        {
            builder.Services.AddSingleton<AgentAccountHubFilter>();
            signalR.AddHubOptions<AgentHub>(options => options.AddFilter<AgentAccountHubFilter>());
        }

        builder.Services.AddGrpc();

        // Agent connection registry: in-memory in all modes. Connection lookups are
        // node-local; an agent that drops simply reconnects and re-registers, so the
        // state is self-healing and needs no persistence. The earlier Postgres-backed
        // HA variant only ever WROTE an agent_connections table that nothing read (all
        // reads are node-local) — dead weight, not a cross-node lookup, so it was
        // removed. HA correctness rests on sticky-session routing; a genuine cross-node
        // registry needs a SignalR backplane (e.g. Redis) and is deferred until then.
        // The in-memory registry still tracks the per-connection account so the dispatch
        // cross-account guard (P3-8 Phase 5) works.
        builder.Services.AddSingleton<IAgentConnectionRegistry, InMemoryAgentConnectionRegistry>();
        builder.Services.AddSingleton<AgentJwtService>();
        builder.Services.AddSingleton<ITargetStatusNotifier, InMemoryTargetStatusNotifier>();
        builder.Services.AddSingleton<TargetStatusPublisher>();
        builder.Services.AddSingleton<ServerAgentUpdateService>();
        builder.Services.AddSingleton<LicenseService>();
        // ILicenseGate forwards to the same LicenseService instance — the
        // data layer enforces quotas through this interface so it stays
        // free of the JWT / RSA dependency chain.
        builder.Services.AddSingleton<ILicenseGate>(
            sp => sp.GetRequiredService<LicenseService>());
        // Cached snapshot of target + user counts for the banner. In multi-account,
        // Scoped (bounded to one account's request/circuit) — a process-wide Singleton
        // serves one tenant's counts to another (cross-account leak). Single-instance
        // keeps the shared Singleton cache. Mirrors the cache services scoped in
        // AddKrakenDeployData; a per-account-keyed cache is the deferred P3-5 step.
        if (multiAccountEnabled)
        {
            builder.Services.AddScoped<LicenseUsageCounter>();
        }
        else
        {
            builder.Services.AddSingleton<LicenseUsageCounter>();
        }
        builder.Services.AddSingleton<DiagnosticsService>();
        // Backup schedule applicator (M13.G). Scoped because it pulls
        // BackupService (scoped) which pulls the DbContextFactory.
        builder.Services.AddScoped<BackupScheduler>();
        // Streaming CSV / JSON export of audit_entries (M13.A.1). Scoped
        // because each export opens its own DbContext.
        builder.Services.AddScoped<AuditExportService>();
        // Global Tasks page aggregator — composes deployments + runbook runs
        // (Space-scoped DB) with Hangfire system jobs (instance-wide). Scoped
        // to compose the scoped Deployment/Runbook services directly.
        builder.Services.AddScoped<ServerTasksService>();
        builder.Services.AddHostedService<DeploymentWorker>();
        builder.Services.AddHostedService<RunbookRunWorker>();
        // Blue-green slot telemetry (docs/blue-green-slot-deployment.md §5): the
        // in-flight dispatch gauge + live circuit counter this instance reports on
        // /slot-metrics so a Draining release can be retired at zero. The counter is
        // one shared singleton, surfaced to Blazor via the CircuitHandler service.
        builder.Services.AddSingleton<InFlightWorkGauge>();
        builder.Services.AddSingleton<KrakenDeploy.Server.Telemetry.CircuitCounter>();
        builder.Services.AddSingleton<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler>(
            sp => sp.GetRequiredService<KrakenDeploy.Server.Telemetry.CircuitCounter>());
        builder.Services.AddSingleton<ServerScriptStepRunner>();
        builder.Services.AddSingleton<DeployReleaseStepRunner>();
        // Shared offline-drop bundle builder — single source of truth for the
        // plan build + gates the worker uses at dispatch AND the UI/API use to
        // regenerate. Singleton (stateless bar ILogger) so the singleton
        // DeploymentWorker can depend on it without a captive dependency; scoped
        // collaborators are resolved from the caller's IServiceProvider.
        builder.Services.AddSingleton<OfflineDropBundleBuilder>();
        builder.Services.AddSingleton<IPendingSubPlanRegistry, PendingSubPlanRegistry>();
        // M11.E.7 — per-target adhoc-script dispatch + result collation.
        builder.Services.AddSingleton<IPendingAdhocRegistry, PendingAdhocRegistry>();
        builder.Services.AddSingleton<IAdhocAgentPusher, HubContextAdhocAgentPusher>();
        builder.Services.AddSingleton<IAdhocDispatcher, AdhocDispatcher>();
        // M11.E commits 3 + 5 — LLM-driven generation + verdict + the session
        // orchestrator. Signing key is loaded lazily on first approval.
        builder.Services.AddScoped<KrakenDeploy.Server.Data.Services.Ai.Adhoc.AdhocGenerationService>();
        builder.Services.AddScoped<KrakenDeploy.Server.Data.Services.Ai.Adhoc.AdhocVerdictService>();
        builder.Services.AddSingleton<KrakenDeploy.Server.Data.Services.Ai.Adhoc.AdhocSigningKeyProvider>();
        builder.Services.AddScoped<AdhocSessionService>();

        // M11.C — autonomous failure diagnosis. The orchestrator drops failed
        // (started) deployment ids on the channel; the worker drains it +
        // runs the best-effort AI diagnosis off the deployment hot path.
        builder.Services.AddSingleton<DeploymentDiagnosisChannel>();
        builder.Services.AddHostedService<DeploymentDiagnosisWorker>();

        // ── M11.B — Model Context Protocol server ────────────────────────────
        // Mounts an in-process MCP server (Streamable HTTP transport) on
        // /mcp. Reuses the existing ApiKey auth scheme; per-Space
        // McpEnabled flag (on SpaceAiSettings) gates traffic. Tools and
        // Resources are discovered by attribute scan over KrakenDeploy.Mcp,
        // so adding new ones is a code-only change — no DI bookkeeping.
        builder.Services.AddKrakenMcp();

        // ── Agent auto-update settings ────────────────────────────────────────
        builder.Services.Configure<AgentUpdateSettings>(
            builder.Configuration.GetSection("AgentUpdate"));

        // ── Authorization ────────────────────────────────────────────────────
        // Cookie-or-ApiKey must be named on BOTH policies:
        //  - the FALLBACK policy covers endpoints with no auth metadata at all;
        //  - the DEFAULT policy covers bare .RequireAuthorization() / [Authorize]
        //    endpoints (/mcp, /api/spaces, /logout, UiHub). A policy without
        //    schemes runs only the DEFAULT scheme (the cookie), so X-Api-Key
        //    callers were 302'd to /login there — verified empirically on
        //    .NET 10. perm:{Permission} policies name the same pair inside
        //    PermissionPolicyProvider. AgentJwt stays separate via
        //    [Authorize(AuthenticationSchemes = "AgentJwt")] on AgentHub.
        var cookieOrApiKey = new AuthorizationPolicyBuilder()
            .AddAuthenticationSchemes(
                IdentityConstants.ApplicationScheme,
                ApiKeyAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .Build();
        builder.Services.AddAuthorizationBuilder()
            .SetDefaultPolicy(cookieOrApiKey)
            .SetFallbackPolicy(cookieOrApiKey);

        // Permission policy provider — builds a one-requirement policy on
        // demand for any policy name "perm:{Permission}". Means we don't have
        // to register 100+ policies up front; .RequirePermission(p) just works.
        builder.Services.AddSingleton<
            Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider,
            PermissionPolicyProvider>();
        builder.Services.AddScoped<
            Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
            PermissionAuthorizationHandler>();
        // Execution-time authorization guard for interactive Blazor handlers —
        // re-checks permission server-side (bypassCache) at action time so a
        // stale/raced RequirePermission UI gate can't authorize a privileged
        // circuit-invoked mutation. Scoped: lives with the circuit, like the UI.
        builder.Services.AddScoped<UiActionGuard>();

        // ── OpenTelemetry ────────────────────────────────────────────────────
        // Tracing and metrics are wired; console exporter is enabled in
        // Development only.  Production exporters (Jaeger, Prometheus, OTLP)
        // are added in a later phase.
        var serviceVersion =
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(rb => rb
                .AddService(serviceName: "KrakenDeploy.Server", serviceVersion: serviceVersion))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (builder.Environment.IsDevelopment())
                {
                    tracing.AddConsoleExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (builder.Environment.IsDevelopment())
                {
                    metrics.AddConsoleExporter();
                }
            });

        // ── Hangfire ─────────────────────────────────────────────────────────
        // Storage on Postgres (Hangfire auto-creates its own schema). The recurring
        // schedule is control-plane fan-out (PerAccountRecurringJobRunner enumerates the
        // catalog and runs each job under WithAccount), so in multi-account the job store
        // lives in the CATALOG / control-plane DB — never a per-tenant DB and never the
        // shared base KrakenDb (which holds nothing tenant-specific under DB-per-account).
        // Single-instance keeps it in KrakenDb (the one app DB). Catalog is validated
        // non-null in the multi-account branch above.
        var hangfireConnectionString = multiAccountEnabled
            ? builder.Configuration.GetConnectionString("Catalog")!
            : connectionString;
        builder.Services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(opt =>
                opt.UseNpgsqlConnection(hangfireConnectionString)));

        // Hangfire worker count — read from PerformanceSettings (M13.F.3).
        // Hangfire's WorkerCount is a builder-time setting; changes from the
        // /configuration/performance page take effect on next server restart.
        // We resolve via a temp scope here because the DI container isn't
        // built yet; a DB failure (first-run / migration pending) falls back
        // to the hardcoded default so startup still succeeds.
        var workerCount = ResolveHangfireWorkerCount(builder);
        builder.Services.AddHangfireServer(options =>
        {
            options.WorkerCount = workerCount;
            options.ServerName  = $"kraken:{Environment.MachineName}";
        });

        // ── HttpClient for outbound calls ────────────────────────────────────
        // Named client used by StepTemplateCatalogService to talk to the
        // GitHub API + raw.githubusercontent.com. The User-Agent header is
        // mandatory for the GitHub API; the optional token raises the
        // unauthenticated 60-req/hr limit to 5000-req/hr when configured.
        builder.Services.AddHttpClient(StepTemplateCatalogService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.Add("User-Agent", "KrakenDeploy/1.0 (+catalog-poll)");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            var token = builder.Configuration["GitHub:Token"];
            if (!string.IsNullOrWhiteSpace(token))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
        });

        // ── Blazor UI ────────────────────────────────────────────────────────
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddRadzenComponents();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents(options =>
            {
                options.DetailedErrors = builder.Environment.IsDevelopment();
            });

        // Minimal-API JSON: tolerate EF navigation cycles. Endpoints that return
        // entity graphs with a bidirectional navigation (e.g. TagSet.Tags ↔
        // Tag.TagSet, populated by EF relationship fix-up on Include) would
        // otherwise throw "possible object cycle detected" → 500. IgnoreCycles
        // writes null at the back-reference; non-cyclic graphs are unchanged.
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.ReferenceHandler =
                System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles);

        // ── Build & configure pipeline ────────────────────────────────────────
        var app = builder.Build();

        if (app.Environment.IsDevelopment() && multiAccountEnabled)
        {
            // Multi-account dev seed: migrate the catalog, ensure a dev shard, and
            // provision demo accounts (each provisions + migrates + seeds its own
            // tenant DB). The single-DB seed below is skipped — there is no single
            // tenant DB in this mode.
            await ControlPlaneDevSeed.SeedAsync(app.Services, app.Configuration, app.Logger)
                .ConfigureAwait(false);
        }
        else if (app.Environment.IsDevelopment())
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
            await db.Database.MigrateAsync().ConfigureAwait(false);

            // Envelope encryption (M13.D.2): generate the wrapped DEK on first
            // boot + eagerly unwrap it (fail-fast if the KEK is wrong). Must run
            // after migrate (the table must exist) and before anything encrypts.
            await scope.ServiceProvider.GetRequiredService<IDekProvider>()
                .EnsureDekAsync().ConfigureAwait(false);

            // Defensive: ensure the Default Space exists. The AddSpacesFoundation
            // migration seeds it, but a fresh DB created by `EnsureCreated()` (or
            // a corrupted seed) would leave us without one.
            var spaceService = scope.ServiceProvider.GetRequiredService<SpaceService>();
            await spaceService.EnsureDefaultAsync().ConfigureAwait(false);

            // Seed built-in roles + teams (RBAC). Idempotent; re-applies role
            // permission sets so seeder edits ship with the upgrade. Must run
            // after EnsureDefaultAsync because per-Space teams need the Default
            // Space to exist first.
            var rbacSeeder = scope.ServiceProvider.GetRequiredService<BuiltInRbacSeeder>();
            await rbacSeeder.SeedAsync().ConfigureAwait(false);

            // Seed built-in step templates (Kraken.IIS, etc.). Idempotent.
            var seeder = scope.ServiceProvider.GetRequiredService<BuiltInStepTemplateSeeder>();
            await seeder.SeedAsync().ConfigureAwait(false);

            // Seed built-in step packages (.kdeploy-step archives shipped
            // alongside the server binary). Idempotent — only installs
            // packages whose (name, version) isn't already in the catalog.
            var pkgSeeder = scope.ServiceProvider.GetRequiredService<BuiltInStepPackageSeeder>();
            await pkgSeeder.SeedAsync().ConfigureAwait(false);

            await PrintFirstRunHintIfNoUsersAsync(scope.ServiceProvider, app.Logger)
                .ConfigureAwait(false);
        }
        else
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        // The shared static key died with M13.C.4 — per-user keys replace it.
        // A leftover value is inert (the handler never reads it), but the
        // operator clearly expects it to work, so say so loudly.
        if (!string.IsNullOrWhiteSpace(app.Configuration["ApiKey:Key"]))
        {
            app.Logger.LogWarning(
                "ApiKey:Key is configured but NO LONGER USED — per-user API keys " +
                "replaced the shared static key (M13.C.4). Remove the config value " +
                "and mint keys via Configuration → API Keys or " +
                "'KrakenDeploy.Server.dll apikeys create'.");
        }

        app.UseHttpsRedirection();

        // Serilog request logging — writes one structured log line per HTTP request.
        // Must come before auth middleware so it captures the full request duration.
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "HTTP {RequestMethod} {RequestPath} responded {StatusCode} " +
                "in {Elapsed:0.0} ms";
        });

        // Cross-customer boundary (multi-account / SaaS only): resolve the active
        // account from the request subdomain and pin it onto IAccountContext BEFORE
        // authentication — Identity loads the user from the per-account tenant DB
        // (users are isolated per account, D4/D9), so the account (and thus the
        // tenant connection) must be resolved first. Fails closed on unknown subdomains.
        if (multiAccountEnabled)
        {
            app.UseMiddleware<AccountResolutionMiddleware>();
        }

        // Space-in-URL routing: redirect a BARE page path (clean entry URL, old
        // bookmark, post-login returnUrl) to the Default Space — BEFORE auth, so the
        // auth challenge fires on the real /s/{slug}/… page and 302-redirects to
        // /login (a bare "/" would otherwise 401 without redirecting). Skips the
        // API/framework/auth/static surface (SpaceRouting.IsSpaceAgnostic).
        app.UseMiddleware<KrakenDeploy.Server.Spaces.SpaceUrlRedirectMiddleware>();

        app.UseAuthentication();
        app.UseAuthorization();

        // Enforces the per-endpoint "agent-register" policy below. No global
        // limiter is configured, so this only affects endpoints that opt in.
        app.UseRateLimiter();

        // M11.B — per-Space MCP-enabled gate. Path-scoped to /mcp so other
        // endpoints sharing the API key (the /api/* surface) keep working
        // when MCP is off. Mounted AFTER UseAuthorization so unauthorised
        // callers never hit the gate (they 401 first); short-circuits with
        // 403 + a clear JSON body when McpEnabled is off for the Space.
        app.UseKrakenMcpEnabledGate();
        app.UseAntiforgery();

        // Maintenance gate (M13.A.3) — MUST be after auth so the
        // middleware can check the caller's BypassMaintenance permission.
        // MUST be before the page / API routing so a non-bypassed user
        // can't reach a write endpoint while the gate is on.
        app.UseMaintenanceMode();

        // Hangfire dashboard — SystemAdmin-only (enforced by HangfireDashboardAuthFilter).
        // Must be placed after UseAuthentication / UseAuthorization so the auth
        // middleware has already run and HttpContext.User is populated.
        app.UseHangfireDashboard("/hangfire", new DashboardOptions
        {
            DashboardTitle = "KrakenDeploy — Background Jobs",
            Authorization = [new HangfireDashboardAuthFilter()],
        });

        app.MapStaticAssets().AllowAnonymous();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .AllowAnonymous();

        app.MapPost("/logout", async (SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync().ConfigureAwait(false);
            return Results.Redirect("/login");
        }).RequireAuthorization(); // any authenticated user can sign out

        // OIDC challenge entry point — the login page links here with
        // ?provider=oidc_{guid}&returnUrl={url}.  We validate the scheme exists
        // before issuing the challenge to block open-redirect abuse.
        app.MapGet("/login/external", async (
            string provider,
            string? returnUrl,
            HttpContext http,
            IAuthenticationSchemeProvider schemeProvider,
            KrakenDeploy.Server.Core.Domain.Accounts.IAccountContext accountContext,
            KrakenDeploy.Server.Data.Services.FeatureFlagService featureFlags) =>
        {
            // M13.F.5 master kill-switch — when OFF, refuse the challenge
            // even if the scheme exists. Local accounts still work; this
            // is the incident-response lever for "an IdP misconfig is
            // locking everyone out, let me sign in with the bootstrap
            // admin and fix it."
            var oidcAllowed = await featureFlags.IsEnabledAsync("security.allow-oidc-sign-in");
            if (!oidcAllowed)
            {
                return Results.Redirect("/login?error=oidc_disabled");
            }

            var scheme = await schemeProvider.GetSchemeAsync(provider);
            if (scheme is null || !provider.StartsWith("oidc_", StringComparison.Ordinal))
            {
                return Results.Redirect("/login?error=unknown_provider");
            }

            // Multi-account defense in depth: the requested scheme must belong to the
            // account resolved from the host, so one tenant's login page cannot initiate
            // another tenant's IdP challenge. (The OIDC correlation cookie is host-only,
            // so a cross-account challenge would fail at callback anyway — we reject it up
            // front and explicitly.)
            if (accountContext.IsResolved
                && KrakenDeploy.Server.Auth.OidcRegistrar.TryParseMultiAccountScheme(
                       provider, out var schemeAccountId, out _)
                && schemeAccountId != accountContext.CurrentAccountId)
            {
                return Results.Redirect("/login?error=unknown_provider");
            }

            var safeReturn = KrakenDeploy.Server.Web.LocalRedirect.MakeSafe(returnUrl);

            var props = new AuthenticationProperties { RedirectUri = safeReturn };
            await http.ChallengeAsync(provider, props).ConfigureAwait(false);
            return Results.Empty;
        }).AllowAnonymous();

        app.MapHub<AgentHub>("/hubs/agent");
        app.MapHub<UiHub>("/hubs/ui");

        // M11.B — MCP Streamable HTTP transport. The endpoint itself
        // requires authentication via the existing ApiKey scheme; the
        // per-Space McpEnabled gate runs in middleware above.
        app.MapKrakenMcp();
        app.MapGrpcService<GrpcPackageDeliveryService>();
        app.MapGrpcService<GrpcStepPackageDeliveryService>();
        app.MapGrpcService<GrpcArtifactUploadService>();

        // Agent self-registration — exchanges a one-time token for a long-lived JWT.
        // Intentionally AllowAnonymous: the token itself is the credential.
        app.MapPost("/api/agents/register",
            async (
                RegisterAgentRequest req,
                TargetRegistrationService registrationSvc,
                AgentJwtService jwtSvc,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req.Token))
                {
                    return Results.BadRequest(new { error = "Token is required." });
                }

                var target = await registrationSvc
                    .ValidateAndConsumeTokenAsync(req.Token, ct)
                    .ConfigureAwait(false);

                if (target is null)
                {
                    return Results.Unauthorized();
                }

                var jwt = jwtSvc.Issue(target.Id);
                return Results.Ok(new RegisterAgentResponse(
                    target.Id, jwt, target.TransportMode.ToString()));
            }).AllowAnonymous().RequireRateLimiting("agent-register");

        // NOTE: the agent REST API (heartbeat / status / logs / complete /
        // pending-work) that mirrored the SignalR hub for the Direct and Polling
        // transports was removed — KrakenDeploy is SignalR-only for live agents,
        // which use the hub methods on AgentHub directly.

        // Agent auto-update endpoints authenticate via the API-key OR agent-JWT scheme.
        // Pass a BUILT policy, not scheme-name strings: RequireAuthorization(params string[])
        // treats its args as POLICY names, and no "ApiKey"/"AgentJwt" policy is registered
        // (those are authentication schemes) — the string overload therefore 500s at the
        // authorization middleware ("AuthorizationPolicy named 'ApiKey' was not found").
        var agentUpdateAuthPolicy = new AuthorizationPolicyBuilder(
                ApiKeyAuthenticationHandler.SchemeName, "AgentJwt")
            .RequireAuthenticatedUser()
            .Build();

        // Agent auto-update — returns whether a newer agent version is available
        // for the given runtime identifier. Called periodically by connected agents.
        app.MapGet("/api/agents/update-info",
            async (
                string rid,
                string currentVersion,
                ServerAgentUpdateService updateSvc,
                HttpContext http,
                CancellationToken ct) =>
            {
                // Resolve the target id from the agent JWT so we can check the
                // per-target opt-out flag.  If we can't resolve it (e.g. no auth),
                // don't leak manifest details but still return no-update.
                var targetIdClaim = http.User.FindFirst(
                    System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (targetIdClaim is null || !Guid.TryParse(targetIdClaim, out var targetId))
                {
                    return Results.Json(new AgentUpdateInfo(false, null, null, null, null));
                }

                var db = http.RequestServices.GetRequiredService<KrakenDbContext>();
                var target = await db.DeploymentTargets
                    .FindAsync(new object[] { targetId }, ct)
                    .ConfigureAwait(false);

                if (target is null || !target.AutoUpdateEnabled)
                {
                    return Results.Json(new AgentUpdateInfo(false, null, null, null, null));
                }

                var manifest = updateSvc.GetManifest();
                if (manifest?.Rids is null || !manifest.Rids.TryGetValue(rid, out var ridInfo))
                {
                    return Results.Json(new AgentUpdateInfo(false, null, null, null, null));
                }

                var latest = manifest.LatestVersion;
                var updateAvailable = !string.Equals(
                    currentVersion, ridInfo.Version, StringComparison.OrdinalIgnoreCase);

                return Results.Ok(new AgentUpdateInfo(
                    updateAvailable,
                    ridInfo.Version,
                    updateAvailable ? $"/api/agents/download/{rid}" : null,
                    ridInfo.SizeBytes,
                    updateAvailable ? ridInfo.Sha256 : null));
            }).RequireAuthorization(agentUpdateAuthPolicy);

        // Agent binary download — serves the self-contained agent archive for the
        // given RID. The caller must already know the RID from the update-info response.
        app.MapGet("/api/agents/download/{rid}",
            (string rid, ServerAgentUpdateService updateSvc) =>
            {
                var download = updateSvc.OpenDownload(rid);
                if (download is null)
                {
                    return Results.NotFound(new { error = $"No agent binary found for RID '{rid}'." });
                }

                var (stream, fileName, contentType) = download.Value;
                return Results.Stream(stream, contentType, fileName,
                    enableRangeProcessing: true);
            }).RequireAuthorization(agentUpdateAuthPolicy);

        // Blue-green slot telemetry (docs/blue-green-slot-deployment.md §5): this
        // instance's live-circuit + in-flight-dispatch counts, plus the release id
        // it runs (stamped per slot instance via Release:Id / Release__Id env at
        // deploy time). Queried by the drain-watcher directly on the slot's own
        // port — deliberately touches NO tenant DbContext so it answers on any
        // host in any mode. Numbers only; anonymous like /healthz.
        app.MapGet("/slot-metrics",
            (
                IConfiguration config,
                KrakenDeploy.Server.Telemetry.CircuitCounter circuits,
                InFlightWorkGauge inFlight) =>
                Results.Ok(new
                {
                    release = config["Release:Id"],
                    activeCircuits = circuits.ActiveCircuits,
                    inFlightDeployments = inFlight.Count,
                })).AllowAnonymous();

        app.MapGet("/healthz",
            async (
                KrakenDbContext db,
                IAgentConnectionRegistry registry,
                CancellationToken ct) =>
            {
                var canConnect = await db.Database.CanConnectAsync(ct).ConfigureAwait(false);
                if (!canConnect)
                {
                    return Results.Json(
                        new { status = "unhealthy", reason = "database unreachable" },
                        statusCode: 503);
                }

                var targets = await db.DeploymentTargets.CountAsync(ct).ConfigureAwait(false);
                return Results.Ok(new
                {
                    status = "ok",
                    targets,
                    connectedAgents = registry.Count,
                });
            }).AllowAnonymous();

        // ── Project API (CLI / REST) ─────────────────────────────────────────
        // ── Spaces API ──────────────────────────────────────────────────────────
        app.MapGet("/api/spaces",
            async (ClaimsPrincipal user, SpaceService spaceSvc,
                   IPermissionEvaluator perms, CancellationToken ct) =>
            {
                // Hard tenant boundary: list only the Spaces the caller can access
                // (all Active Spaces for system admins; otherwise the Spaces they
                // reach via real team membership). Never the full cross-Space list.
                var accessible = await perms.GetAccessibleSpaceIdsAsync(user, ct).ConfigureAwait(false);
                var spaces = await spaceSvc.GetAllAsync(ct).ConfigureAwait(false);
                return Results.Ok(spaces.Where(s => accessible.Contains(s.Id)).ToList());
            }).RequireAuthorization();

        // Switching the active Space is now a plain navigation to /s/{slug}/… —
        // no server endpoint needed. The slug is validated against the caller's
        // accessible Spaces (the hard tenant boundary) by
        // SpaceScopedComponentBase on every page; SpaceUrlRedirectMiddleware only
        // 302-redirects a bare path to the Default Space (no cookie, no last-used).

        app.MapPost("/api/spaces",
            async (CreateSpaceRequest req, ClaimsPrincipal user,
                   SpaceService spaceSvc, CancellationToken ct) =>
            {
                try
                {
                    // Anti-lockout: seed the creator into the new Space's "Space
                    // Managers" team so a non-admin creator isn't shut out by the
                    // hard tenant boundary. System admins reach it via AdministerSystem.
                    var creatorId = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var uid)
                        ? uid : (Guid?)null;
                    var space = await spaceSvc.CreateAsync(req.Slug, req.Name, req.Description, creatorId, ct)
                        .ConfigureAwait(false);
                    return Results.Created($"/api/spaces/{space.Id}", space);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(new { error = ex.Message });
                }
            }).RequirePermission(Permission.SpaceCreate);

        app.MapPut("/api/spaces/{id:guid}",
            async (Guid id, UpdateSpaceRequest req, SpaceService spaceSvc, CancellationToken ct) =>
            {
                var space = await spaceSvc.UpdateAsync(id, req.Name, req.Description, ct)
                    .ConfigureAwait(false);
                return space is null ? Results.NotFound() : Results.Ok(space);
            }).RequirePermission(Permission.SpaceEdit);

        app.MapPost("/api/spaces/{id:guid}/archive",
            async (Guid id, SpaceService spaceSvc, CancellationToken ct) =>
            {
                try
                {
                    var ok = await spaceSvc.ArchiveAsync(id, ct).ConfigureAwait(false);
                    return ok ? Results.Ok(new { archived = true }) : Results.NotFound();
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.SpaceDelete);

        // ── AI settings (Phase M11.A.6.3) ───────────────────────────────────
        // Per-Space AI provider + budget + feature flags. The {id} route param
        // is the Space id, but the actual scoping happens via ISpaceContext
        // (the ambient Space — driven by the /s/{slug} route in the browser; the
        // /api surface itself is Default-scoped). Keeping {id} in the URL makes the
        // endpoint trivially CLIable + URL-greppable.
        app.MapGet("/api/spaces/{id:guid}/ai-settings",
            async (Guid id,
                   KrakenDeploy.Server.Data.Services.Ai.SpaceAiSettingsService svc,
                   CancellationToken ct) =>
                Results.Ok(await svc.GetAsync(ct).ConfigureAwait(false))
        ).RequirePermission(Permission.SpaceAiSettingsView);

        app.MapPut("/api/spaces/{id:guid}/ai-settings",
            async (Guid id,
                   KrakenDeploy.Server.Data.Services.Ai.UpdateSpaceAiSettingsRequest req,
                   KrakenDeploy.Server.Data.Services.Ai.SpaceAiSettingsService svc,
                   IAuditLog audit,
                   CancellationToken ct) =>
            {
                try
                {
                    var dto = await svc.UpdateAsync(req, ct).ConfigureAwait(false);
                    // Redact ApiKey from the audit payload — capture the
                    // intent (changed / cleared / preserved) without
                    // recording any form of the secret itself.
                    var apiKeyAction = string.IsNullOrWhiteSpace(req.ApiKey)
                        ? "preserved"
                        : req.ApiKey == KrakenDeploy.Server.Data.Services.Ai.SpaceAiSettingsService.ApiKeyClearSentinel
                            ? "cleared"
                            : "changed";
                    await audit.RecordAsync(
                        AuditEventType.SpaceAiSettingsUpdated,
                        subjectType: "SpaceAiSettings",
                        subjectId:   id.ToString(),
                        details:     $"Provider={req.Provider}, Model={req.Model}, " +
                                     $"Budget=${req.BudgetUsdPerMonth}, ApiKey={apiKeyAction}, " +
                                     $"Diagnosis={req.DiagnosisEnabled}, Mcp={req.McpEnabled}, " +
                                     $"Adhoc={req.AdhocEnabled}, Assistant={req.AssistantEnabled}, " +
                                     $"LogBodies={req.LogPromptBodies}",
                        ct: ct).ConfigureAwait(false);
                    return Results.Ok(dto);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.SpaceAiSettingsManage);

        app.MapGet("/api/spaces/{id:guid}/ai-settings/api-key",
            async (Guid id,
                   KrakenDeploy.Server.Data.Services.Ai.SpaceAiSettingsService svc,
                   IAuditLog audit,
                   CancellationToken ct) =>
            {
                var key = await svc.RevealApiKeyAsync(ct).ConfigureAwait(false);
                // Reveal is logged on every call regardless of outcome —
                // operators reading the key IS the sensitive operation.
                await audit.RecordAsync(
                    AuditEventType.SpaceAiApiKeyRevealed,
                    subjectType: "SpaceAiSettings",
                    subjectId:   id.ToString(),
                    details:     key is null ? "key not configured" : "key revealed",
                    ct: ct).ConfigureAwait(false);
                return Results.Ok(new { apiKey = key });
            }).RequirePermission(Permission.SpaceAiSettingsManage);

        app.MapGet("/api/spaces/{id:guid}/ai-settings/usage",
            async (Guid id,
                   KrakenDeploy.Server.Data.Services.Ai.SpaceAiSettingsService svc,
                   CancellationToken ct) =>
                Results.Ok(await svc.GetUsageAsync(ct).ConfigureAwait(false))
        ).RequirePermission(Permission.SpaceAiSettingsView);

        app.MapGet("/api/projects",
            async (ProjectService projectSvc, CancellationToken ct) =>
                Results.Ok(await projectSvc.GetAllAsync(ct).ConfigureAwait(false))
        ).RequirePermission(Permission.ProjectView);

        app.MapGet("/api/projects/{id:guid}",
            async (Guid id, ProjectService projectSvc, CancellationToken ct) =>
            {
                var project = await projectSvc.GetAsync(id, ct).ConfigureAwait(false);
                return project is null ? Results.NotFound() : Results.Ok(project);
            }).RequirePermission(Permission.ProjectView);

        app.MapGet("/api/projects/by-slug/{slug}",
            async (string slug, ProjectService projectSvc, CancellationToken ct) =>
            {
                var project = await projectSvc.GetBySlugAsync(slug, ct).ConfigureAwait(false);
                return project is null ? Results.NotFound() : Results.Ok(project);
            }).RequirePermission(Permission.ProjectView);

        // ── Environment API (CLI / REST) ─────────────────────────────────────
        app.MapGet("/api/environments",
            async (EnvironmentService envSvc, CancellationToken ct) =>
                Results.Ok(await envSvc.GetAllOrderedAsync(ct).ConfigureAwait(false))
        ).RequirePermission(Permission.EnvironmentView);

        // ── Target API (CLI / REST) ──────────────────────────────────────────
        app.MapGet("/api/targets",
            async (TargetService targetSvc, CancellationToken ct) =>
                Results.Ok(await targetSvc.GetAllAsync(ct).ConfigureAwait(false))
        ).RequirePermission(Permission.MachineView);

        // ── Package API ──────────────────────────────────────────────────────
        // Upload a package: POST /api/packages/upload
        // Body: multipart/form-data with fields packageId, version, and file.
        app.MapPost("/api/packages/upload",
            async (HttpRequest req, PackageService packageSvc, CancellationToken ct) =>
            {
                if (!req.HasFormContentType)
                {
                    return Results.BadRequest(new { error = "Multipart form required." });
                }

                var form = await req.ReadFormAsync(ct).ConfigureAwait(false);
                var packageId = form["packageId"].ToString();
                var version = form["version"].ToString();
                var file = form.Files["file"];

                if (string.IsNullOrWhiteSpace(packageId) ||
                    string.IsNullOrWhiteSpace(version) ||
                    file is null)
                {
                    return Results.BadRequest(
                        new { error = "packageId, version, and file are required." });
                }

                try
                {
                    await using var stream = file.OpenReadStream();
                    var pkg = await packageSvc
                        .UploadAsync(packageId, version, file.FileName, stream, ct)
                        .ConfigureAwait(false);
                    return Results.Ok(new
                    {
                        pkg.Id, pkg.PackageId, pkg.Version,
                        pkg.FileName, pkg.SizeBytes, pkg.UploadedUtc,
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(new { error = ex.Message });
                }
            }).RequirePermission(Permission.PackageEdit);

        app.MapGet("/api/packages",
            async (PackageService packageSvc, CancellationToken ct) =>
                Results.Ok(await packageSvc.GetSummariesAsync(ct).ConfigureAwait(false))
        ).RequirePermission(Permission.PackageView);

        app.MapGet("/api/packages/{packageId}/versions",
            async (string packageId, PackageService packageSvc, CancellationToken ct) =>
                Results.Ok(await packageSvc.GetVersionsAsync(packageId, ct).ConfigureAwait(false))
        ).RequirePermission(Permission.PackageView);

        app.MapDelete("/api/packages/{id:guid}",
            async (Guid id, PackageService packageSvc, CancellationToken ct) =>
            {
                var deleted = await packageSvc.DeleteAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequirePermission(Permission.PackageDelete);

        // Download a package by id (browser triggers Save As via the
        // Content-Disposition attachment header).
        app.MapGet("/api/packages/{id:guid}/download",
            async (Guid id, PackageService packageSvc, CancellationToken ct) =>
            {
                var (stream, package) = await packageSvc.OpenStreamAsync(id, ct).ConfigureAwait(false);
                return Results.File(stream, "application/octet-stream", package.FileName);
            }).RequirePermission(Permission.PackageView);

        // Download by package-id + version — convenience for "highest version"
        // links and external integrations that don't know the row id.
        app.MapGet("/api/packages/{packageId}/{version}/download",
            async (string packageId, string version, PackageService packageSvc,
                   CancellationToken ct) =>
            {
                var pkg = await packageSvc.GetAsync(packageId, version, ct).ConfigureAwait(false);
                if (pkg is null)
                {
                    return Results.NotFound();
                }

                var (stream, package) = await packageSvc.OpenStreamAsync(pkg.Id, ct).ConfigureAwait(false);
                return Results.File(stream, "application/octet-stream", package.FileName);
            }).RequirePermission(Permission.PackageView);

        // ── Process API ──────────────────────────────────────────────────────
        app.MapGet("/api/projects/{projectId:guid}/process",
            async (Guid projectId, ProcessService processSvc, CancellationToken ct) =>
            {
                var process = await processSvc.GetAsync(projectId, ct).ConfigureAwait(false);
                return process is null ? Results.NotFound() : Results.Ok(process);
            }).RequirePermission(Permission.ProcessView);

        app.MapPost("/api/projects/{projectId:guid}/process/steps",
            async (Guid projectId, AddStepRequest req, ProcessService processSvc, CancellationToken ct) =>
            {
                var step = await processSvc.AddStepAsync(
                    projectId, req.Name, req.StepType, req.PackageId,
                    req.TargetRoles, req.Config,
                    req.StepPackageName, req.StepPackageVersion,
                    knobs: null, ct: ct).ConfigureAwait(false);
                return Results.Created($"/api/projects/{projectId}/process/steps/{step.Id}", step);
            }).RequirePermission(Permission.ProcessEdit);

        app.MapDelete("/api/projects/{projectId:guid}/process/steps/{stepId:guid}",
            async (Guid projectId, Guid stepId, ProcessService processSvc, CancellationToken ct) =>
            {
                var removed = await processSvc.RemoveStepAsync(stepId, ct).ConfigureAwait(false);
                return removed ? Results.NoContent() : Results.NotFound();
            }).RequirePermission(Permission.ProcessEdit);

        app.MapPost("/api/projects/{projectId:guid}/process/import-octopus",
            async (Guid projectId, ImportDeploymentProcessRequest req,
                ProcessService processSvc, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req.Json))
                {
                    return Results.BadRequest(new { error = "Json is required." });
                }
                try
                {
                    var summary = await processSvc
                        .ImportDeploymentProcessAsync(projectId, req.Json, req.Replace, ct)
                        .ConfigureAwait(false);
                    return Results.Ok(summary);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.ProcessEdit);

        // ── Release API ──────────────────────────────────────────────────────
        app.MapGet("/api/projects/{projectId:guid}/releases",
            async (Guid projectId, ReleaseService releaseSvc, CancellationToken ct) =>
                Results.Ok(await releaseSvc.GetAllAsync(projectId, ct).ConfigureAwait(false))
        ).RequirePermission(Permission.ReleaseView);

        app.MapPost("/api/projects/{projectId:guid}/releases",
            async (Guid projectId, CreateReleaseRequest req, ReleaseService releaseSvc,
                CancellationToken ct) =>
            {
                try
                {
                    var release = await releaseSvc.CreateAsync(
                        projectId, req.Version, req.PackageVersions, req.ReleaseNotes, req.ChannelId, ct)
                        .ConfigureAwait(false);
                    return Results.Created(
                        $"/api/projects/{projectId}/releases/{release.Id}", release);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(new { error = ex.Message });
                }
            }).RequirePermission(Permission.ReleaseCreate);

        // Octopus-style "Update Variables" — re-snapshot the project's
        // current variable set into an existing release. Gated by
        // Permission.ReleaseEdit (same level required to delete a release).
        app.MapPost("/api/releases/{releaseId:guid}/update-variables",
            async (Guid releaseId, ReleaseService releaseSvc, CancellationToken ct) =>
            {
                try
                {
                    var release = await releaseSvc
                        .UpdateVariablesAsync(releaseId, ct).ConfigureAwait(false);
                    return Results.Ok(new
                    {
                        release.Id,
                        release.Version,
                        release.VariableSnapshotUpdatedUtc,
                        VariableCount = release.VariableSnapshot.Count,
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.NotFound(new { error = ex.Message });
                }
            }).RequirePermission(Permission.ReleaseEdit);

        // ── Variable API ─────────────────────────────────────────────────────
        app.MapGet("/api/projects/{projectId:guid}/variables",
            async (Guid projectId, VariableService variableSvc, CancellationToken ct) =>
                Results.Ok(await variableSvc.GetVariablesAsync(projectId, ct).ConfigureAwait(false))
        ).RequirePermission(Permission.VariableView);

        app.MapPost("/api/projects/{projectId:guid}/variables",
            async (Guid projectId, UpsertVariableRequest req,
                VariableService variableSvc, CancellationToken ct) =>
            {
                if (!Enum.TryParse<VariableType>(req.Type, ignoreCase: true, out var type))
                {
                    return Results.BadRequest(new
                    {
                        error = $"Unknown variable type '{req.Type}'. Valid: String, Sensitive, StringArray.",
                    });
                }

                var scope = new VariableScope
                {
                    TenantId = req.ScopeTenantId,
                    EnvironmentId = req.ScopeEnvironmentId,
                    TargetId = req.ScopeTargetId,
                    Roles = req.ScopeRoles,
                    ChannelId = req.ScopeChannelId,
                    StepName = req.ScopeStepName,
                };

                try
                {
                    var variable = await variableSvc
                        .CreateVariableAsync(projectId, req.Name, req.Value, type, scope, ct)
                        .ConfigureAwait(false);

                    return Results.Created(
                        $"/api/projects/{projectId}/variables/{variable.Id}",
                        new { variable.Id, variable.Name, Type = variable.Type.ToString(), variable.Scope });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.VariableEdit);

        app.MapPut("/api/projects/{projectId:guid}/variables/{variableId:guid}",
            async (Guid projectId, Guid variableId, UpsertVariableRequest req,
                VariableService variableSvc, CancellationToken ct) =>
            {
                if (!Enum.TryParse<VariableType>(req.Type, ignoreCase: true, out var type))
                {
                    return Results.BadRequest(new
                    {
                        error = $"Unknown variable type '{req.Type}'. Valid: String, Sensitive, StringArray.",
                    });
                }

                var scope = new VariableScope
                {
                    TenantId = req.ScopeTenantId,
                    EnvironmentId = req.ScopeEnvironmentId,
                    TargetId = req.ScopeTargetId,
                    Roles = req.ScopeRoles,
                    ChannelId = req.ScopeChannelId,
                    StepName = req.ScopeStepName,
                };

                var variable = await variableSvc
                    .UpdateVariableAsync(variableId, req.Name, req.Value, type, scope, ct)
                    .ConfigureAwait(false);

                return variable is null ? Results.NotFound() : Results.Ok(variable);
            }).RequirePermission(Permission.VariableEdit);

        app.MapDelete("/api/projects/{projectId:guid}/variables/{variableId:guid}",
            async (Guid projectId, Guid variableId, VariableService variableSvc, CancellationToken ct) =>
            {
                var deleted = await variableSvc.DeleteVariableAsync(variableId, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequirePermission(Permission.VariableEdit);

        // ── Step-template API ────────────────────────────────────────────────
        app.MapGet("/api/step-templates",
            async (StepTemplateService svc, CancellationToken ct) =>
            {
                var templates = await svc.GetAllAsync(ct).ConfigureAwait(false);
                var summaries = templates.Select(t => new StepTemplateSummaryDto(
                    t.Id, t.Name, t.Description, t.ActionType,
                    t.Parameters.Count, t.Version, t.CreatedUtc));
                return Results.Ok(summaries);
            }).RequirePermission(Permission.StepTemplateView);

        app.MapGet("/api/step-templates/{id:guid}",
            async (Guid id, StepTemplateService svc, CancellationToken ct) =>
            {
                var template = await svc.GetAsync(id, ct).ConfigureAwait(false);
                return template is null ? Results.NotFound() : Results.Ok(template);
            }).RequirePermission(Permission.StepTemplateView);

        app.MapPost("/api/step-templates",
            async (CreateStepTemplateRequest req, StepTemplateService svc,
                CancellationToken ct) =>
            {
                var parameters = req.Parameters?.Select(p =>
                    new StepTemplateParameter
                    {
                        Name          = p.Name,
                        Label         = p.Label,
                        HelpText      = p.HelpText,
                        DefaultValue  = p.DefaultValue,
                        ControlType   = p.ControlType,
                        SelectOptions = p.SelectOptions ?? [],
                    }).ToList();

                var template = await svc.CreateAsync(
                    req.Name, req.ActionType, req.Description,
                    req.Properties, parameters, ct)
                    .ConfigureAwait(false);

                return Results.Created($"/api/step-templates/{template.Id}", template);
            }).RequirePermission(Permission.StepTemplateCreate);

        app.MapPut("/api/step-templates/{id:guid}",
            async (Guid id, UpdateStepTemplateRequest req, StepTemplateService svc,
                CancellationToken ct) =>
            {
                var parameters = req.Parameters?.Select(p =>
                    new StepTemplateParameter
                    {
                        Name          = p.Name,
                        Label         = p.Label,
                        HelpText      = p.HelpText,
                        DefaultValue  = p.DefaultValue,
                        ControlType   = p.ControlType,
                        SelectOptions = p.SelectOptions ?? [],
                    }).ToList();

                var template = await svc.UpdateAsync(
                    id, req.Name, req.Description, req.Properties, parameters, ct)
                    .ConfigureAwait(false);

                return template is null ? Results.NotFound() : Results.Ok(template);
            }).RequirePermission(Permission.StepTemplateEdit);

        app.MapDelete("/api/step-templates/{id:guid}",
            async (Guid id, StepTemplateService svc, CancellationToken ct) =>
            {
                var deleted = await svc.DeleteAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequirePermission(Permission.StepTemplateDelete);

        app.MapPost("/api/step-templates/import",
            async (ImportStepTemplateRequest req, StepTemplateService svc,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req.Json))
                {
                    return Results.BadRequest(new { error = "Json is required." });
                }

                try
                {
                    var template = await svc.ImportFromJsonAsync(
                        req.Json, req.ImportSource, ct: ct)
                        .ConfigureAwait(false);
                    return Results.Ok(template);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.StepTemplateCreate);

        // ── Audit log streaming export (M13.A.1) ─────────────────────────────
        // Two endpoints — CSV (Excel-friendly) + JSON (round-trippable) —
        // both stream the filtered audit rows directly to the response body
        // without buffering the full result set. Filter params mirror the
        // /audit page UI: from/to/eventType/user/subjectType.

        static AuditExportService.Filter ParseAuditExportFilter(HttpRequest req)
        {
            DateTimeOffset? Parse(string? s)
                => string.IsNullOrWhiteSpace(s)
                    ? null
                    : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

            // The page UI sends "to" as an inclusive day boundary; the
            // service treats it as exclusive (< toUtc). Caller already
            // adds the +1 day, so we just pass it through.
            return new AuditExportService.Filter(
                FromUtc:              Parse(req.Query["from"]),
                ToUtcExclusive:       Parse(req.Query["to"]),
                EventTypeContains:    req.Query["eventType"].FirstOrDefault(),
                UserDisplayContains:  req.Query["user"].FirstOrDefault(),
                SubjectTypeContains:  req.Query["subjectType"].FirstOrDefault());
        }

        // ── Diagnostics zip download (M13.A.2) ───────────────────────────────
        // Permission-gated on ConfigureServer (same tier as License / SMTP /
        // Features pages). The zip is built in-memory then streamed; it's
        // small (a few KB JSON + a 1000-line log tail) so a buffered build
        // is fine.
        app.MapGet("/api/diagnostics/report.zip",
            async (HttpContext ctx, DiagnosticsService svc) =>
            {
                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                ctx.Response.ContentType = "application/zip";
                ctx.Response.Headers.ContentDisposition =
                    $"attachment; filename=\"kraken-diagnostics-{stamp}.zip\"";
                await svc.WriteDiagnosticsReportZipAsync(ctx.Response.Body, ctx.RequestAborted)
                    .ConfigureAwait(false);
            }).RequirePermission(Permission.ConfigureServer);

        app.MapGet("/api/audit/export.csv",
            async (HttpContext ctx, AuditExportService svc) =>
            {
                var filter = ParseAuditExportFilter(ctx.Request);
                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                ctx.Response.ContentType = "text/csv; charset=utf-8";
                ctx.Response.Headers.ContentDisposition =
                    $"attachment; filename=\"audit-{stamp}.csv\"";
                await svc.WriteCsvAsync(ctx.Response.Body, filter, ctx.RequestAborted)
                    .ConfigureAwait(false);
            }).RequirePermission(Permission.EventView);

        app.MapGet("/api/audit/export.json",
            async (HttpContext ctx, AuditExportService svc) =>
            {
                var filter = ParseAuditExportFilter(ctx.Request);
                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.Headers.ContentDisposition =
                    $"attachment; filename=\"audit-{stamp}.json\"";
                await svc.WriteJsonAsync(ctx.Response.Body, filter, ctx.RequestAborted)
                    .ConfigureAwait(false);
            }).RequirePermission(Permission.EventView);

        app.MapGet("/api/step-templates/{id:guid}/export",
            async (Guid id, StepTemplateService svc, CancellationToken ct) =>
            {
                var template = await svc.GetAsync(id, ct).ConfigureAwait(false);
                if (template is null)
                {
                    return Results.NotFound();
                }
                var json = OctopusLibraryExporter.Serialize(template);
                var safeName = string.Concat(template.Name
                    .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' '))
                    .Replace(' ', '_');
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    safeName = id.ToString("N");
                }
                return Results.File(System.Text.Encoding.UTF8.GetBytes(json),
                    contentType: "application/json",
                    fileDownloadName: $"{safeName}.json");
            }).RequirePermission(Permission.StepTemplateView);

        app.MapPost("/api/step-templates/import-folder",
            async (ImportFolderRequest req, StepTemplateService svc,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req.FolderPath))
                {
                    return Results.BadRequest(new { error = "FolderPath is required." });
                }

                try
                {
                    var summary = await svc.ImportFromDirectoryAsync(req.FolderPath, ct)
                        .ConfigureAwait(false);
                    return Results.Ok(summary);
                }
                catch (DirectoryNotFoundException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (UnauthorizedAccessException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.StepTemplateCreate);

        app.MapPost("/api/step-templates/import-octopus-api",
            async (ImportOctopusApiRequest req, StepTemplateService svc,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req.Json))
                {
                    return Results.BadRequest(new { error = "Json is required." });
                }

                try
                {
                    var summary = await svc.ImportFromOctopusApiResponseAsync(req.Json, ct)
                        .ConfigureAwait(false);
                    return Results.Ok(summary);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.StepTemplateCreate);

        // ── Community catalog API ────────────────────────────────────────────
        app.MapGet("/api/step-template-catalog",
            async (string? category, StepTemplateCatalogService svc, CancellationToken ct) =>
            {
                var entries = await svc.GetAllAsync(category, ct).ConfigureAwait(false);
                var lastSync = await svc.GetLastSyncAsync(ct).ConfigureAwait(false);
                return Results.Ok(new { entries, lastSync });
            }).RequirePermission(Permission.StepTemplateView);

        app.MapPost("/api/step-template-catalog/refresh",
            async (StepTemplateCatalogService svc, CancellationToken ct) =>
            {
                try
                {
                    var summary = await svc.RefreshAsync(ct).ConfigureAwait(false);
                    return Results.Ok(summary);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.StepTemplateCreate);

        app.MapPost("/api/step-template-catalog/{id:guid}/install",
            async (Guid id, StepTemplateCatalogService svc, CancellationToken ct) =>
            {
                try
                {
                    var template = await svc.InstallAsync(id, ct).ConfigureAwait(false);
                    return Results.Ok(template);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.StepTemplateCreate);

        // ── Step Packages (Phase D — .kdeploy-step plugins) ──────────────────

        app.MapGet("/api/step-packages",
            async (string? name, StepPackageService svc, CancellationToken ct) =>
            {
                var rows = string.IsNullOrWhiteSpace(name)
                    ? await svc.GetAllAsync(ct).ConfigureAwait(false)
                    : await svc.GetVersionsAsync(name, ct).ConfigureAwait(false);
                return Results.Ok(rows);
            }).RequirePermission(Permission.StepPackageView);

        app.MapGet("/api/step-packages/{id:guid}",
            async (Guid id, StepPackageService svc, CancellationToken ct) =>
            {
                var row = await svc.GetAsync(id, ct).ConfigureAwait(false);
                return row is null ? Results.NotFound() : Results.Ok(row);
            }).RequirePermission(Permission.StepPackageView);

        // Multipart upload of a .kdeploy-step archive. Form field name is
        // "file"; size cap is 64 MB (RequestSizeLimitAttribute equivalent).
        app.MapPost("/api/step-packages",
            async (HttpRequest req, StepPackageService svc, CancellationToken ct) =>
            {
                if (!req.HasFormContentType)
                {
                    return Results.BadRequest(new { error =
                        "Request must be multipart/form-data with a 'file' field carrying the .kdeploy-step archive." });
                }
                var form = await req.ReadFormAsync(ct).ConfigureAwait(false);
                var file = form.Files["file"]
                    ?? (form.Files.Count > 0 ? form.Files[0] : null);
                if (file is null || file.Length == 0)
                {
                    return Results.BadRequest(new { error = "No file uploaded." });
                }
                await using var stream = file.OpenReadStream();
                var result = await svc.UploadAsync(stream, ct: ct).ConfigureAwait(false);
                if (!result.Success)
                {
                    return Results.BadRequest(new { error = result.ErrorMessage });
                }
                return Results.Created($"/api/step-packages/{result.Installed!.Id}", result.Installed);
            }).RequirePermission(Permission.StepPackageManage)
              .DisableAntiforgery();

        // Uninstall a specific version. Returns:
        //   204 No Content                — package removed.
        //   409 Conflict + ConflictReport — live or snapshotted references still pin this version.
        //   404 Not Found                 — no row at this (name, version).
        app.MapDelete("/api/step-packages/{name}/{version}",
            async (string name, string version,
                   StepPackageService svc, IAuditLog audit, CancellationToken ct) =>
            {
                var result = await svc.UninstallAsync(name, version, ct).ConfigureAwait(false);
                return result.Status switch
                {
                    StepPackageService.UninstallStatus.Uninstalled => await EmitUninstallAuditAsync(),
                    StepPackageService.UninstallStatus.Blocked     => Results.Conflict(result.Conflicts),
                    StepPackageService.UninstallStatus.NotFound    => Results.NotFound(),
                    _ => Results.StatusCode(500),
                };

                async Task<IResult> EmitUninstallAuditAsync()
                {
                    await audit.RecordAsync(
                        AuditEventType.StepPackageUninstalled,
                        subjectType: nameof(StepPackage),
                        subjectId: $"{name}@{version}",
                        subjectName: $"{name} {version}",
                        details: $"Removed step package '{name}' version '{version}'.",
                        ct: ct).ConfigureAwait(false);
                    return Results.NoContent();
                }
            }).RequirePermission(Permission.StepPackageManage);

        // ── Step-package catalog (Phase D-9) ─────────────────────────────────
        // GitHub-feed mirror of KrakenDeploy/StepPackages. Hourly Hangfire
        // poll keeps the catalog table fresh; admins can install / refresh
        // on demand here.

        app.MapGet("/api/step-package-catalog",
            async (StepPackageCatalogService catalog, CancellationToken ct) =>
                Results.Ok(await catalog.GetAllAsync(ct).ConfigureAwait(false))
        ).RequirePermission(Permission.StepPackageView);

        app.MapPost("/api/step-package-catalog/refresh",
            async (StepPackageCatalogService catalog, CancellationToken ct) =>
            {
                try
                {
                    var result = await catalog.RefreshAsync(ct).ConfigureAwait(false);
                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
                }
            }).RequirePermission(Permission.StepPackageManage);

        // ── Step-package usage + bulk upgrade (Phase D-10) ───────────────────

        app.MapGet("/api/step-packages/{name}/usage",
            async (string name, StepPackageService svc, CancellationToken ct) =>
                Results.Ok(await svc.GetUsageAsync(name, ct).ConfigureAwait(false))
        ).RequirePermission(Permission.StepPackageView);

        app.MapPost("/api/step-packages/{name}/bulk-upgrade",
            async (string name, BulkUpgradeRequest req,
                   StepPackageService svc, IAuditLog audit, CancellationToken ct) =>
            {
                try
                {
                    var result = await svc.BulkUpgradeAsync(
                        name,
                        req.TargetVersion,
                        req.DeploymentStepIds ?? [],
                        req.RunbookStepIds ?? [],
                        ct).ConfigureAwait(false);

                    // One audit event for the bulk action — captures the package,
                    // target version, and the count of touched steps. Individual
                    // step row changes are also picked up by the EF
                    // AuditableEntityInterceptor on DeploymentStep/RunbookStep.
                    await audit.RecordAsync(
                        AuditEventType.StepPackageBulkUpgraded,
                        subjectType: nameof(StepPackage),
                        subjectId: $"{name}@{req.TargetVersion}",
                        subjectName: $"{name} → {req.TargetVersion}",
                        details: $"Bulk upgrade: touched={result.Touched}, skipped={result.Skipped.Count}.",
                        ct: ct).ConfigureAwait(false);

                    return Results.Ok(result);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.StepPackageManage);

        app.MapPost("/api/step-package-catalog/{name}/{version}/install",
            async (string name, string version,
                   StepPackageCatalogService catalog, IAuditLog audit, CancellationToken ct) =>
            {
                try
                {
                    var installed = await catalog.InstallAsync(name, version, ct).ConfigureAwait(false);
                    await audit.RecordAsync(
                        AuditEventType.StepPackageInstalled,
                        subjectType: nameof(StepPackage),
                        subjectId: $"{installed.Name}@{installed.Version}",
                        subjectName: $"{installed.Name} {installed.Version}",
                        details: $"Installed from catalog (pull from GitHub feed).",
                        ct: ct).ConfigureAwait(false);
                    return Results.Created($"/api/step-packages/{installed.Id}", installed);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
                }
            }).RequirePermission(Permission.StepPackageManage);

        // ── Deployment API ───────────────────────────────────────────────────
        app.MapGet("/api/deployments",
            async (Guid? projectId, DeploymentService deploymentSvc, CancellationToken ct) =>
                Results.Ok(await deploymentSvc.GetAllAsync(projectId, ct: ct).ConfigureAwait(false))
        ).RequirePermission(Permission.DeploymentView);

        app.MapGet("/api/deployments/{id:guid}",
            async (Guid id, DeploymentService deploymentSvc, CancellationToken ct) =>
            {
                var d = await deploymentSvc.GetAsync(id, ct).ConfigureAwait(false);
                return d is null ? Results.NotFound() : Results.Ok(d);
            }).RequirePermission(Permission.DeploymentView);

        // Returns log entries for a deployment, optionally filtered by sequence number.
        // The CLI --wait flag polls this endpoint and prints new lines incrementally.
        app.MapGet("/api/deployments/{id:guid}/logs",
            async (Guid id, DeploymentService deploymentSvc, CancellationToken ct, int from = 0) =>
            {
                var d = await deploymentSvc.GetAsync(id, ct).ConfigureAwait(false);
                if (d is null)
                {
                    return Results.NotFound();
                }

                var entries = d.LogEntries
                    .Where(e => e.Sequence >= from)
                    .OrderBy(e => e.Sequence)
                    .Select(e => new
                    {
                        e.Sequence,
                        e.Timestamp,
                        e.Level,
                        e.Message,
                    });

                return Results.Ok(entries);
            }).RequirePermission(Permission.DeploymentView);

        app.MapPost("/api/deployments",
            async (TriggerDeploymentRequest req, DeploymentService deploymentSvc,
                CancellationToken ct) =>
            {
                try
                {
                    var deployment = await deploymentSvc
                        .CreateAsync(
                            releaseId:     req.ReleaseId,
                            environmentId: req.EnvironmentId,
                            targetId:      req.TargetId,
                            tenantId:      req.TenantId,
                            scheduledFor:  req.ScheduledFor,
                            failureMode:   req.FailureMode,
                            ct:            ct)
                        .ConfigureAwait(false);
                    return Results.Created($"/api/deployments/{deployment.Id}", deployment);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.DeploymentCreate);

        // Cancel a Queued or Running deployment. A queued one never dispatches;
        // a running one stops at the next wave boundary (the agent protocol has
        // no in-flight abort) — see DeploymentService.CancelAsync. Gated on
        // TaskCancel (Octopus models a deployment as a cancellable task).
        app.MapPost("/api/deployments/{id:guid}/cancel",
            async (Guid id, DeploymentService deploymentSvc, IAuditLog audit,
                CancellationToken ct) =>
            {
                try
                {
                    var deployment = await deploymentSvc.CancelAsync(id, ct).ConfigureAwait(false);
                    if (deployment is null)
                    {
                        return Results.NotFound();
                    }

                    await audit.RecordAsync(
                        AuditEventType.DeploymentCancelled,
                        subjectType: "Deployment",
                        subjectId:   id.ToString(),
                        details:     "Deployment cancelled via API.",
                        ct:          ct).ConfigureAwait(false);

                    return Results.Ok(new { deployment.Id, Status = deployment.Status.ToString() });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.TaskCancel);

        // ── Artifact API ─────────────────────────────────────────────────────────
        app.MapGet("/api/deployments/{id:guid}/artifacts",
            async (Guid id, ArtifactService artifactSvc, CancellationToken ct) =>
                Results.Ok(await artifactSvc.GetByDeploymentAsync(id, ct).ConfigureAwait(false))
        ).RequirePermission(Permission.ArtifactView);

        app.MapGet("/api/deployments/{deploymentId:guid}/artifacts/{artifactId:guid}/download",
            async (Guid deploymentId, Guid artifactId,
                ArtifactService artifactSvc, CancellationToken ct) =>
            {
                try
                {
                    var (stream, artifact) = await artifactSvc
                        .OpenReadAsync(artifactId, ct).ConfigureAwait(false);
                    return Results.Stream(stream, artifact.ContentType,
                        fileDownloadName: artifact.FileName, enableRangeProcessing: true);
                }
                catch (InvalidOperationException)
                {
                    return Results.NotFound();
                }
            }).RequirePermission(Permission.ArtifactDownload);

        // ── Offline Drop API ────────────────────────────────────────────────────────

        app.MapGet("/api/deployments/{id:guid}/drop-bundle",
            async (Guid id, DeploymentService deploymentSvc,
                IConfiguration config, CancellationToken ct) =>
            {
                var deployment = await deploymentSvc.GetAsync(id, ct).ConfigureAwait(false);
                if (deployment is null)
                {
                    return Results.NotFound();
                }

                if (string.IsNullOrEmpty(deployment.DropBundlePath))
                {
                    return Results.NotFound(new { error = "No drop bundle available for this deployment." });
                }

                var dataPath = config["DataPath"] ?? "data";
                try
                {
                    var stream = DropBundleService.OpenRead(deployment.DropBundlePath, dataPath);
                    return Results.Stream(stream, "application/zip",
                        fileDownloadName: $"drop-{id}.zip", enableRangeProcessing: true);
                }
                catch (FileNotFoundException)
                {
                    return Results.NotFound(new { error = "Drop bundle file not found on disk." });
                }
            }).RequirePermission(Permission.DeploymentView);

        // Regenerate the drop bundle for an offline-drop deployment still
        // awaiting its result (e.g. the operator lost the file, or a package
        // was re-uploaded). Re-materialises a secret-bearing deployable, so
        // gate at DeploymentCreate rather than the read-only DeploymentView.
        app.MapPost("/api/deployments/{id:guid}/regenerate-drop-bundle",
            async (Guid id, OfflineDropBundleBuilder bundleBuilder, HttpContext http,
                CancellationToken ct) =>
            {
                try
                {
                    await bundleBuilder
                        .RegenerateForDeploymentAsync(id, http.RequestServices, ct)
                        .ConfigureAwait(false);
                    return Results.Ok(new { id, regenerated = true });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.DeploymentCreate);

        app.MapPost("/api/deployments/{id:guid}/offline-result",
            async (Guid id, HttpRequest request, OfflineResultService resultSvc,
                CancellationToken ct) =>
            {
                if (!request.HasFormContentType || request.Form.Files.Count == 0)
                {
                    return Results.BadRequest(new { error = "Upload a result bundle zip file." });
                }

                var file = request.Form.Files[0];
                try
                {
                    await using var stream = file.OpenReadStream();
                    var deployment = await resultSvc.IngestAsync(id, stream, ct)
                        .ConfigureAwait(false);
                    return Results.Ok(new
                    {
                        deployment.Id,
                        Status = deployment.Status.ToString(),
                        deployment.CompletedUtc,
                    });
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.OfflineResultUpload).DisableAntiforgery();

        app.MapPost("/api/targets/{id:guid}/offline-drop-config",
            async (Guid id, SaveOfflineDropConfigRequest req,
                TargetService targetSvc,
                KrakenDeploy.Server.Core.Domain.Variables.IEncryptionService encryption,
                CancellationToken ct) =>
            {
                var target = await targetSvc.GetAsync(id, ct).ConfigureAwait(false);
                if (target is null)
                {
                    return Results.NotFound();
                }

                var cfg = target.OfflineDropConfig ?? new KrakenDeploy.Server.Core.Domain.Targets.OfflineDropConfig();
                cfg.DeliveryChannel = req.DeliveryChannel;

                // SMTP
                cfg.SmtpHost = req.SmtpHost;
                cfg.SmtpPort = req.SmtpPort;
                cfg.SmtpUseSsl = req.SmtpUseSsl;
                cfg.SmtpUsername = req.SmtpUsername;
                cfg.SmtpPasswordEncrypted = !string.IsNullOrEmpty(req.SmtpPassword)
                    ? encryption.Encrypt(req.SmtpPassword)
                    : cfg.SmtpPasswordEncrypted;
                cfg.SmtpRecipient = req.SmtpRecipient;
                cfg.SmtpSender = req.SmtpSender;

                // Webhook
                cfg.WebhookUrl = req.WebhookUrl;
                cfg.WebhookSecretEncrypted = !string.IsNullOrEmpty(req.WebhookSecret)
                    ? encryption.Encrypt(req.WebhookSecret)
                    : cfg.WebhookSecretEncrypted;

                // File share
                cfg.FileSharePath = req.FileSharePath;
                cfg.FileShareUsername = req.FileShareUsername;
                cfg.FileSharePasswordEncrypted = !string.IsNullOrEmpty(req.FileSharePassword)
                    ? encryption.Encrypt(req.FileSharePassword)
                    : cfg.FileSharePasswordEncrypted;

                target.OfflineDropConfig = cfg;
                await targetSvc.UpdateAsync(target, ct).ConfigureAwait(false);
                return Results.Ok(new { saved = true });
            }).RequirePermission(Permission.MachineEdit);

        app.MapPost("/api/targets/{id:guid}/generate-hmac-key",
            async (Guid id, TargetService targetSvc,
                KrakenDeploy.Server.Core.Domain.Variables.IEncryptionService encryption,
                IAuditLog audit,
                CancellationToken ct) =>
            {
                var target = await targetSvc.GetAsync(id, ct).ConfigureAwait(false);
                if (target is null)
                {
                    return Results.NotFound();
                }

                var cfg = target.OfflineDropConfig ?? new KrakenDeploy.Server.Core.Domain.Targets.OfflineDropConfig();
                var rawKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                cfg.HmacKeyEncrypted = encryption.Encrypt(Convert.ToBase64String(rawKey));
                target.OfflineDropConfig = cfg;
                await targetSvc.UpdateAsync(target, ct).ConfigureAwait(false);

                // Rotation invalidates in-flight bundles — record who/when for forensics.
                await audit.RecordAsync(
                    AuditEventType.OfflineDropHmacKeyGenerated,
                    subjectType: "DeploymentTarget",
                    subjectId:   id.ToString(),
                    subjectName: target.Name,
                    details:     "Offline-drop HMAC signing key (re)generated.",
                    ct:          ct).ConfigureAwait(false);
                return Results.Ok(new { hmacKeyGenerated = true });
            }).RequirePermission(Permission.MachineEdit);

        app.MapPost("/api/targets/{id:guid}/generate-bundle-key",
            async (Guid id, TargetService targetSvc,
                KrakenDeploy.Server.Core.Domain.Variables.IEncryptionService encryption,
                IAuditLog audit,
                CancellationToken ct) =>
            {
                var target = await targetSvc.GetAsync(id, ct).ConfigureAwait(false);
                if (target is null)
                {
                    return Results.NotFound();
                }

                var cfg = target.OfflineDropConfig ?? new KrakenDeploy.Server.Core.Domain.Targets.OfflineDropConfig();
                var rawKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                var base64Key = Convert.ToBase64String(rawKey);
                cfg.BundleKeyEncrypted = encryption.Encrypt(base64Key);
                target.OfflineDropConfig = cfg;
                await targetSvc.UpdateAsync(target, ct).ConfigureAwait(false);

                // The raw key is disclosed once; rotation makes existing bundles
                // undecryptable — record the disclosure/rotation for forensics.
                await audit.RecordAsync(
                    AuditEventType.OfflineDropBundleKeyGenerated,
                    subjectType: "DeploymentTarget",
                    subjectId:   id.ToString(),
                    subjectName: target.Name,
                    details:     "Offline-drop bundle encryption key (re)generated and disclosed once.",
                    ct:          ct).ConfigureAwait(false);

                // Returned ONCE so an operator can deliver it out-of-band to the
                // offline target (the runner needs it to decrypt plan.enc). The
                // server only ever persists the encrypted form.
                return Results.Ok(new { bundleKey = base64Key });
            }).RequirePermission(Permission.MachineEdit);

        // ── Tenant API ─────────────────────────────────────────────────────────────

        app.MapGet("/api/tenants",
            async (TenantService tenantSvc, CancellationToken ct) =>
                Results.Ok(await tenantSvc.GetAllAsync(ct).ConfigureAwait(false)))
            .RequirePermission(Permission.TenantView);

        app.MapGet("/api/tenants/{id:guid}",
            async (Guid id, TenantService tenantSvc, CancellationToken ct) =>
            {
                var tenant = await tenantSvc.GetAsync(id, ct).ConfigureAwait(false);
                return tenant is null ? Results.NotFound() : Results.Ok(tenant);
            }).RequirePermission(Permission.TenantView);

        app.MapPost("/api/tenants",
            async (CreateTenantRequest req, TenantService tenantSvc, CancellationToken ct) =>
            {
                try
                {
                    var tenant = await tenantSvc.CreateAsync(req.Name, req.Slug, req.Description, ct)
                        .ConfigureAwait(false);
                    return Results.Created($"/api/tenants/{tenant.Id}", tenant);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.TenantCreate);

        app.MapPut("/api/tenants/{id:guid}",
            async (Guid id, CreateTenantRequest req, TenantService tenantSvc, CancellationToken ct) =>
            {
                try
                {
                    var tenant = await tenantSvc.UpdateAsync(id, req.Name, req.Slug, req.Description, ct)
                        .ConfigureAwait(false);
                    return tenant is null ? Results.NotFound() : Results.Ok(tenant);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.TenantEdit);

        app.MapDelete("/api/tenants/{id:guid}",
            async (Guid id, TenantService tenantSvc, CancellationToken ct) =>
            {
                var deleted = await tenantSvc.DeleteAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequirePermission(Permission.TenantDelete);

        // Project-Tenant connections
        app.MapPost("/api/tenants/{tenantId:guid}/projects/{projectId:guid}",
            async (Guid tenantId, Guid projectId, TenantService tenantSvc, CancellationToken ct) =>
            {
                try
                {
                    await tenantSvc.ConnectProjectAsync(tenantId, projectId, ct).ConfigureAwait(false);
                    return Results.NoContent();
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.TenantEdit);

        app.MapDelete("/api/tenants/{tenantId:guid}/projects/{projectId:guid}",
            async (Guid tenantId, Guid projectId, TenantService tenantSvc, CancellationToken ct) =>
            {
                await tenantSvc.DisconnectProjectAsync(tenantId, projectId, ct).ConfigureAwait(false);
                return Results.NoContent();
            }).RequirePermission(Permission.TenantEdit);

        // ── Tag Sets API (Space-level extended tag sets) ─────────────────────
        // docs/extended-tag-sets-plan.md — sets carry Scope (entity kinds) and
        // Type (MultiSelect / SingleSelect / FreeText); applications live in
        // the polymorphic tag_applications table.

        app.MapGet("/api/tag-sets",
            async (TagService tagSvc, CancellationToken ct) =>
                Results.Ok(await tagSvc.GetAllSetsAsync(ct).ConfigureAwait(false)))
            .RequirePermission(Permission.TagSetView);

        app.MapGet("/api/tag-sets/{id:guid}",
            async (Guid id, TagService tagSvc, CancellationToken ct) =>
            {
                var set = await tagSvc.GetSetAsync(id, ct).ConfigureAwait(false);
                return set is null ? Results.NotFound() : Results.Ok(set);
            }).RequirePermission(Permission.TagSetView);

        app.MapPost("/api/tag-sets",
            async (CreateTagSetRequest req, TagService tagSvc, CancellationToken ct) =>
            {
                try
                {
                    var set = await tagSvc.CreateSetAsync(
                            req.Name, req.Description, req.Type, req.Scopes ?? [], req.SortOrder, ct)
                        .ConfigureAwait(false);
                    return Results.Created($"/api/tag-sets/{set.Id}", set);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.TagSetCreate);

        // ?force=true confirms destructive scope removal (cascades the removed
        // kind's applications) — mirrors the UI confirm dialog.
        app.MapPut("/api/tag-sets/{id:guid}",
            async (Guid id, CreateTagSetRequest req, TagService tagSvc,
                CancellationToken ct, bool force = false) =>
            {
                try
                {
                    var set = await tagSvc.UpdateSetAsync(
                            id, req.Name, req.Description, req.Type, req.Scopes ?? [],
                            req.SortOrder, force, ct)
                        .ConfigureAwait(false);
                    return set is null ? Results.NotFound() : Results.Ok(set);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.TagSetEdit);

        app.MapDelete("/api/tag-sets/{id:guid}",
            async (Guid id, TagService tagSvc, CancellationToken ct) =>
            {
                var deleted = await tagSvc.DeleteSetAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequirePermission(Permission.TagSetDelete);

        app.MapPost("/api/tag-sets/{tagSetId:guid}/tags",
            async (Guid tagSetId, CreateTagRequest req, TagService tagSvc, CancellationToken ct) =>
            {
                try
                {
                    var tag = await tagSvc.CreateTagAsync(tagSetId, req.Name, req.Color, req.Description, ct)
                        .ConfigureAwait(false);
                    return Results.Created($"/api/tag-sets/{tagSetId}/tags/{tag.Id}", tag);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.TagSetEdit);

        app.MapPut("/api/tag-sets/{tagSetId:guid}/tag-order",
            async (Guid tagSetId, ReorderTagsRequest req, TagService tagSvc, CancellationToken ct) =>
            {
                await tagSvc.ReorderTagsAsync(tagSetId, req.OrderedTagIds ?? [], ct).ConfigureAwait(false);
                return Results.NoContent();
            }).RequirePermission(Permission.TagSetEdit);

        app.MapPut("/api/tags/{id:guid}",
            async (Guid id, CreateTagRequest req, TagService tagSvc, CancellationToken ct) =>
            {
                try
                {
                    var tag = await tagSvc.UpdateTagAsync(id, req.Name, req.Color, req.Description, ct)
                        .ConfigureAwait(false);
                    return tag is null ? Results.NotFound() : Results.Ok(tag);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.TagSetEdit);

        app.MapDelete("/api/tags/{id:guid}",
            async (Guid id, TagService tagSvc, CancellationToken ct) =>
            {
                var deleted = await tagSvc.DeleteTagAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequirePermission(Permission.TagSetEdit);

        // ── Tag applications per entity ──────────────────────────────────────
        // One GET + one PUT per taggable kind, registered in a loop so each
        // route carries the entity's own View/Edit permission statically (no
        // in-handler permission mapping). PUT body: TagIds for select-type
        // sets, FreeTextValue for free-text sets (null clears the value).
        var tagKindRoutes = new (string Segment, KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind Kind,
            Permission View, Permission Edit)[]
        {
            ("tenants",      KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind.Tenant,
                Permission.TenantView,      Permission.TenantEdit),
            ("projects",     KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind.Project,
                Permission.ProjectView,     Permission.ProjectEdit),
            ("environments", KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind.Environment,
                Permission.EnvironmentView, Permission.EnvironmentEdit),
            ("runbooks",     KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind.Runbook,
                Permission.RunbookView,     Permission.RunbookEdit),
            ("targets",      KrakenDeploy.Server.Core.Domain.Tags.TaggableEntityKind.DeploymentTarget,
                Permission.MachineView,     Permission.MachineEdit),
        };
        foreach (var (segment, kind, viewPerm, editPerm) in tagKindRoutes)
        {
            app.MapGet($"/api/{segment}/{{entityId:guid}}/tags",
                async (Guid entityId, TagService tagSvc, CancellationToken ct) =>
                    Results.Ok(await tagSvc.GetForEntityAsync(kind, entityId, ct).ConfigureAwait(false)))
                .RequirePermission(viewPerm);

            app.MapPut($"/api/{segment}/{{entityId:guid}}/tags/{{tagSetId:guid}}",
                async (Guid entityId, Guid tagSetId, ApplyTagsRequest req,
                    TagService tagSvc, CancellationToken ct) =>
                {
                    try
                    {
                        if (req.TagIds is not null)
                        {
                            await tagSvc.SetAppliedTagsAsync(tagSetId, kind, entityId, req.TagIds, ct)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await tagSvc.SetFreeTextValueAsync(tagSetId, kind, entityId, req.FreeTextValue, ct)
                                .ConfigureAwait(false);
                        }
                        return Results.NoContent();
                    }
                    catch (InvalidOperationException ex)
                    {
                        return Results.BadRequest(new { error = ex.Message });
                    }
                }).RequirePermission(editPerm);
        }

        // ── Lifecycle API ──────────────────────────────────────────────────────────

        app.MapGet("/api/lifecycles",
            async (LifecycleService lcSvc, CancellationToken ct) =>
                Results.Ok(await lcSvc.GetAllAsync(ct).ConfigureAwait(false)))
            .RequirePermission(Permission.LifecycleView);

        app.MapGet("/api/lifecycles/{id:guid}",
            async (Guid id, LifecycleService lcSvc, CancellationToken ct) =>
            {
                var lc = await lcSvc.GetAsync(id, ct).ConfigureAwait(false);
                return lc is null ? Results.NotFound() : Results.Ok(lc);
            }).RequirePermission(Permission.LifecycleView);

        app.MapPost("/api/lifecycles",
            async (CreateLifecycleRequest req, LifecycleService lcSvc, CancellationToken ct) =>
            {
                try
                {
                    var lc = await lcSvc.CreateAsync(req.Name, req.Description, ct).ConfigureAwait(false);
                    return Results.Created($"/api/lifecycles/{lc.Id}", lc);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.LifecycleCreate);

        app.MapPut("/api/lifecycles/{id:guid}",
            async (Guid id, UpdateLifecycleRequest req, LifecycleService lcSvc, CancellationToken ct) =>
            {
                try
                {
                    var lc = await lcSvc.UpdateAsync(id, req.Name, req.Description, req.Phases, ct)
                        .ConfigureAwait(false);
                    return lc is null ? Results.NotFound() : Results.Ok(lc);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.LifecycleEdit);

        app.MapDelete("/api/lifecycles/{id:guid}",
            async (Guid id, LifecycleService lcSvc, CancellationToken ct) =>
            {
                var deleted = await lcSvc.DeleteAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequirePermission(Permission.LifecycleDelete);

        // ── Channel API ────────────────────────────────────────────────────────────

        app.MapGet("/api/projects/{projectId:guid}/channels",
            async (Guid projectId, ChannelService channelSvc, CancellationToken ct) =>
                Results.Ok(await channelSvc.GetForProjectAsync(projectId, ct).ConfigureAwait(false)))
            .RequirePermission(Permission.ChannelView);

        app.MapPost("/api/projects/{projectId:guid}/channels",
            async (Guid projectId, UpsertChannelRequest req, ChannelService channelSvc, CancellationToken ct) =>
            {
                try
                {
                    var ch = await channelSvc.CreateAsync(
                        projectId, req.Name, req.IsDefault, req.LifecycleId,
                        req.VersionRange, req.VersionTag, ct).ConfigureAwait(false);
                    return Results.Created($"/api/projects/{projectId}/channels/{ch.Id}", ch);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.ChannelCreate);

        app.MapPut("/api/channels/{id:guid}",
            async (Guid id, UpsertChannelRequest req, ChannelService channelSvc, CancellationToken ct) =>
            {
                try
                {
                    var ch = await channelSvc.UpdateAsync(
                        id, req.Name, req.IsDefault, req.LifecycleId,
                        req.VersionRange, req.VersionTag, ct).ConfigureAwait(false);
                    return ch is null ? Results.NotFound() : Results.Ok(ch);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.ChannelEdit);

        app.MapDelete("/api/channels/{id:guid}",
            async (Guid id, ChannelService channelSvc, CancellationToken ct) =>
            {
                try
                {
                    var deleted = await channelSvc.DeleteAsync(id, ct).ConfigureAwait(false);
                    return deleted ? Results.NoContent() : Results.NotFound();
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.ChannelDelete);

        // ── Runbook API ────────────────────────────────────────────────────────────

        app.MapGet("/api/projects/{projectId:guid}/runbooks",
            async (Guid projectId, RunbookService runbookSvc, CancellationToken ct) =>
                Results.Ok(await runbookSvc.GetAllAsync(projectId, ct).ConfigureAwait(false)))
            .RequirePermission(Permission.RunbookView);

        app.MapGet("/api/runbooks/{id:guid}",
            async (Guid id, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var rb = await runbookSvc.GetAsync(id, ct).ConfigureAwait(false);
                return rb is null ? Results.NotFound() : Results.Ok(rb);
            }).RequirePermission(Permission.RunbookView);

        app.MapPost("/api/projects/{projectId:guid}/runbooks",
            async (Guid projectId, CreateRunbookRequest req, RunbookService runbookSvc, CancellationToken ct) =>
            {
                try
                {
                    var rb = await runbookSvc.CreateAsync(projectId, req.Name, req.Description, ct)
                        .ConfigureAwait(false);
                    return Results.Created($"/api/runbooks/{rb.Id}", rb);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.RunbookEdit);

        app.MapPut("/api/runbooks/{id:guid}",
            async (Guid id, CreateRunbookRequest req, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var rb = await runbookSvc.UpdateAsync(id, req.Name, req.Description, ct)
                    .ConfigureAwait(false);
                return rb is null ? Results.NotFound() : Results.Ok(rb);
            }).RequirePermission(Permission.RunbookEdit);

        app.MapDelete("/api/runbooks/{id:guid}",
            async (Guid id, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var deleted = await runbookSvc.DeleteAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequirePermission(Permission.RunbookEdit);

        // Runbook steps
        app.MapPost("/api/runbooks/{runbookId:guid}/steps",
            async (Guid runbookId, AddStepRequest req, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var step = await runbookSvc.AddStepAsync(
                    runbookId, req.Name, req.StepType, req.PackageId, req.TargetRoles, req.Config,
                    req.StepPackageName, req.StepPackageVersion, ct: ct)
                    .ConfigureAwait(false);
                return Results.Created($"/api/runbooks/{runbookId}/steps/{step.Id}", step);
            }).RequirePermission(Permission.RunbookEdit);

        app.MapPut("/api/runbook-steps/{stepId:guid}",
            async (Guid stepId, AddStepRequest req, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var step = await runbookSvc.UpdateStepAsync(
                    stepId, req.Name, req.StepType, req.PackageId, req.TargetRoles, req.Config,
                    req.StepPackageName, req.StepPackageVersion, ct: ct)
                    .ConfigureAwait(false);
                return step is null ? Results.NotFound() : Results.Ok(step);
            }).RequirePermission(Permission.RunbookEdit);

        app.MapDelete("/api/runbook-steps/{stepId:guid}",
            async (Guid stepId, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var deleted = await runbookSvc.DeleteStepAsync(stepId, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequirePermission(Permission.RunbookEdit);

        // Runbook runs
        app.MapGet("/api/runbooks/{runbookId:guid}/runs",
            async (Guid runbookId, RunbookService runbookSvc, CancellationToken ct) =>
                Results.Ok(await runbookSvc.GetRunsAsync(runbookId, ct).ConfigureAwait(false)))
            .RequirePermission(Permission.RunbookRunView);

        app.MapGet("/api/runbook-runs/{runId:guid}",
            async (Guid runId, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var run = await runbookSvc.GetRunAsync(runId, ct).ConfigureAwait(false);
                return run is null ? Results.NotFound() : Results.Ok(run);
            }).RequirePermission(Permission.RunbookRunView);

        app.MapPost("/api/runbooks/{runbookId:guid}/runs",
            async (Guid runbookId, TriggerRunbookRunRequest req, RunbookService runbookSvc,
                CancellationToken ct) =>
            {
                try
                {
                    var run = await runbookSvc.TriggerAsync(
                        runbookId, req.EnvironmentId, req.TargetId, req.TenantId, ct)
                        .ConfigureAwait(false);
                    return Results.Created($"/api/runbook-runs/{run.Id}", run);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.RunbookRunCreate);

        // Dev-only: creates a smoke-test target and returns its registration token.
        // Guards behind IsDevelopment so it is never registered in production.
        if (app.Environment.IsDevelopment())
        {
            app.MapPost("/api/dev/smoke-register",
                async (
                    TargetRegistrationService registrationSvc,
                    CancellationToken ct) =>
                {
                    // Dev-only smoke affordance: bypass the license quota gate so
                    // the CI smoke test can register its one agent against a
                    // fresh, license-less DB. Never reachable in production
                    // (endpoint is IsDevelopment-gated).
                    var (_, token) = await registrationSvc
                        .CreateAsync("smoke-agent", ["smoke"], TransportMode.Reverse,
                            bypassLicenseCheck: true, ct)
                        .ConfigureAwait(false);
                    return Results.Ok(new { token });
                }).AllowAnonymous();
        }

        // Register Hangfire recurring jobs after the app is built so the storage
        // is fully initialised.  Safe to call multiple times (AddOrUpdate is
        // idempotent) — on each restart the schedule is refreshed.
        if (multiAccountEnabled)
        {
            // Per-account fan-out: each per-tenant recurring job runs once per active
            // account (inside a WithAccount scope). Re-uses the single-tenant job ids,
            // so AddOrUpdate also REPLACES any stale schedule a prior single-tenant run
            // persisted in Hangfire storage (those would otherwise keep firing without
            // a resolved account).
            HangfireJobRegistrar.RegisterPerAccountRecurringJobs();
        }
        else
        {
            HangfireJobRegistrar.RegisterRecurringJobs();
        }

        // Apply the operator-controlled backup schedule (M13.G). The cron lives in
        // BackupSettings, not in the Registrar above, so this needs its own Apply pass at
        // startup. The settings page calls Apply again after every save.
        // Multi-account: each active account owns a per-account backup job
        // (kraken.backup:{accountId}) reconciled from its own BackupSettings, run under
        // WithAccount against its tenant DB. Single-instance: the single kraken.backup job.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            try
            {
                if (multiAccountEnabled)
                {
                    await scope.ServiceProvider
                        .GetRequiredService<AccountBackupRunner>()
                        .ReconcileSchedulesAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                else
                {
                    await scope.ServiceProvider
                        .GetRequiredService<BackupScheduler>()
                        .ApplyAsync()
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex,
                    "Failed to apply backup schedule(s) at startup — UI can " +
                    "re-save settings to retry.");
            }
        }

        // Validate license on startup — warn in logs but don't block.
        // The UI also shows a banner to System Administrators.
        {
            var licenseSvc = app.Services.GetRequiredService<LicenseService>();
            var result = licenseSvc.LoadAndValidate();
            if (!result.IsValid)
            {
                app.Logger.LogWarning(
                    "License: {Error}. Upload a license key in Settings → License.",
                    result.ErrorMessage);
            }
        }

        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task PrintFirstRunHintIfNoUsersAsync(
        IServiceProvider services,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if (!await userManager.Users.AnyAsync().ConfigureAwait(false))
        {
            logger.LogWarning(
                "No users exist yet. Create an admin with: " +
                "dotnet run --project src/KrakenDeploy.Server -- users create-admin --email <e> --password <p>");
        }
    }

    /// <summary>
    /// Walks up from the assembly directory until it finds <c>appsettings.json</c>.
    /// Handles both development (<c>dotnet run</c>) and production (published binary)
    /// content-root layouts. Also ensures <c>DOTNET_ENVIRONMENT</c> is set so
    /// environment-specific appsettings files (Development / Production) are loaded.
    /// </summary>
    private static string ResolveContentRoot()
    {
        // Default to Development for CLI tools so appsettings.Development.json
        // (which has the real connection string) is loaded. Users override via
        // DOTNET_ENVIRONMENT=Production.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")))
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
        }
        var dir = Path.GetDirectoryName(typeof(Program).Assembly.Location)
            ?? Directory.GetCurrentDirectory();

        while (!File.Exists(Path.Combine(dir, "appsettings.json")))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir)
            {
                // Can't find appsettings — fall back to assembly dir.
                return Path.GetDirectoryName(typeof(Program).Assembly.Location)
                    ?? Directory.GetCurrentDirectory();
            }

            dir = parent;
        }

        return dir;
    }

    /// <summary>
    /// Reads <see cref="KrakenDeploy.Server.Core.Domain.Performance.PerformanceSettings.HangfireWorkerCount"/>
    /// from the DB before the DI container is fully built. Falls back to
    /// the hardcoded default on any failure (first-run, migrations pending,
    /// DB unreachable) so startup never blocks on this knob.
    /// </summary>
    private static int ResolveHangfireWorkerCount(WebApplicationBuilder builder)
    {
        // The Hangfire worker count is a single-process/platform knob. Multi-account has
        // no single tenant DB to read it from — reading an arbitrary tenant's
        // PerformanceSettings would be wrong — so use the default. Single-instance reads
        // PerformanceSettings from the app DB. (This previously read the never-configured
        // "Default" connection name, so the knob never took effect — the real key is
        // "KrakenDb".)
        var multiAccount = builder.Configuration.GetValue(
            $"{MultiAccountOptions.SectionName}:{nameof(MultiAccountOptions.Enabled)}", false);
        var connectionString = multiAccount
            ? null
            : builder.Configuration.GetConnectionString("KrakenDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return KrakenDeploy.Server.Core.Domain.Performance.PerformanceSettings.DefaultHangfireWorkerCount;
        }

        try
        {
            var options = new DbContextOptionsBuilder<KrakenDbContext>()
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .Options;

            // Pass-through ISpaceContext: PerformanceSettings is system-wide,
            // not Space-scoped, so the SpaceScopingInterceptor doesn't apply.
            // Use DefaultSpaceContext so the constructor's contract is satisfied.
            using var db = new KrakenDbContext(
                options,
                new KrakenDeploy.Server.Data.Spaces.DefaultSpaceContext());

            var row = db.PerformanceSettings
                .AsNoTracking()
                .FirstOrDefault(p => p.Id ==
                    KrakenDeploy.Server.Core.Domain.Performance.PerformanceSettings.SingletonId);

            return row?.HangfireWorkerCount
                ?? KrakenDeploy.Server.Core.Domain.Performance.PerformanceSettings.DefaultHangfireWorkerCount;
        }
        catch
        {
            // First-run, migrations pending, or DB unreachable — fall back.
            // Logging here is awkward (ILogger isn't built yet); the
            // hardcoded default preserves previous behaviour.
            return KrakenDeploy.Server.Core.Domain.Performance.PerformanceSettings.DefaultHangfireWorkerCount;
        }
    }
}
