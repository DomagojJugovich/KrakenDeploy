using System.Globalization;
using System.Text;
using Hangfire;
using Hangfire.PostgreSql;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Auth;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Commands;
using KrakenDeploy.Server.Components;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Hangfire;
using KrakenDeploy.Server.Services;
using KrakenDeploy.Server.Transport;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.StepTemplates;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Spaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
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
        builder.Services.AddKrakenDeployData(connectionString, dataPath);
        builder.Services.AddKrakenDeployIdentityCore()
            .AddSignInManager();

        // ── Space context (HTTP-aware override of DefaultSpaceContext) ───────
        // Reads the active Space from the kraken-active-space cookie set by
        // the Space switcher; falls back to WellKnown.DefaultSpaceId.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ISpaceContext, HttpSpaceContext>();

        // ── Encryption (AES-256-GCM for sensitive variables) ────────────────
        // In production, set Encryption:MasterKey to a base64-encoded 32-byte key.
        // Generate with: Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        var masterKey = builder.Configuration["Encryption:MasterKey"];
        if (string.IsNullOrWhiteSpace(masterKey))
        {
            masterKey = Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            Log.Warning(
                "Encryption:MasterKey is not configured — using an ephemeral key. " +
                "Sensitive variables encrypted in this session will be unreadable after restart. " +
                "Set Encryption:MasterKey in appsettings or user-secrets for production.");
        }

        builder.Services.AddSingleton<IEncryptionService>(_ => new AesEncryptionService(masterKey));

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

        // Register one OIDC scheme per enabled IdentityProvider in the DB.
        // Silently no-ops if DB isn't ready yet (first-run before migration).
        OidcRegistrar.RegisterSchemes(builder);

        // Agent JWT bearer — separate scheme so it doesn't conflict with the
        // cookie auth used by the Blazor UI.
        var agentJwtKey = builder.Configuration["Agent:JwtSigningKey"];
        if (string.IsNullOrWhiteSpace(agentJwtKey))
        {
            throw new InvalidOperationException(
                "Agent:JwtSigningKey is not configured. " +
                "Set it in appsettings or user-secrets (minimum 32 characters for HS256).");
        }

        // CLI API-key scheme — validated against ApiKey:Key configuration.
        // When ApiKey:Key is not configured the handler returns NoResult() harmlessly.
        builder.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, _ => { });

        builder.Services.AddAuthentication()
            .AddJwtBearer("AgentJwt", options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(agentJwtKey)),
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
        builder.Services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 1_048_576; // 1 MiB — control plane only
        });

        builder.Services.AddGrpc();

        // Agent connection registry: InMemory for single-server; Postgres-backed
        // for HA pair. Set Server:HaMode to "Postgres" in the 2-node configuration.
        if (string.Equals(builder.Configuration["Server:HaMode"], "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var connStr = builder.Configuration.GetConnectionString("KrakenDb")
                ?? throw new InvalidOperationException("ConnectionStrings:KrakenDb is required for HA mode.");
            builder.Services.AddSingleton<IAgentConnectionRegistry>(
                _ => new PostgresAgentConnectionRegistry(connStr));
        }
        else
        {
            builder.Services.AddSingleton<IAgentConnectionRegistry, InMemoryAgentConnectionRegistry>();
        }
        builder.Services.AddSingleton<AgentJwtService>();
        builder.Services.AddSingleton<ITargetStatusNotifier, InMemoryTargetStatusNotifier>();
        builder.Services.AddSingleton<TargetStatusPublisher>();
        builder.Services.AddSingleton<ServerAgentUpdateService>();
        builder.Services.AddSingleton<LicenseService>();
        builder.Services.AddHostedService<DeploymentWorker>();
        builder.Services.AddHostedService<RunbookRunWorker>();
        builder.Services.AddSingleton<ServerScriptStepRunner>();
        builder.Services.AddSingleton<DeployReleaseStepRunner>();
        builder.Services.AddSingleton<IPendingSubPlanRegistry, PendingSubPlanRegistry>();

        // ── Agent auto-update settings ────────────────────────────────────────
        builder.Services.Configure<AgentUpdateSettings>(
            builder.Configuration.GetSection("AgentUpdate"));

        // ── Authorization ────────────────────────────────────────────────────
        // The fallback policy covers all endpoints that call RequireAuthorization().
        // Specifying multiple schemes here tells the authorization middleware to try
        // each one in turn; the first success wins.  AgentJwt is handled separately
        // via [Authorize(AuthenticationSchemes = "AgentJwt")] on AgentHub.
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .AddAuthenticationSchemes(
                    IdentityConstants.ApplicationScheme,
                    ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .Build());

        // Permission policy provider — builds a one-requirement policy on
        // demand for any policy name "perm:{Permission}". Means we don't have
        // to register 100+ policies up front; .RequirePermission(p) just works.
        builder.Services.AddSingleton<
            Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider,
            PermissionPolicyProvider>();
        builder.Services.AddScoped<
            Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
            PermissionAuthorizationHandler>();

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
        // Storage on Postgres (same database as the app — Hangfire auto-creates
        // its own schema).  The background server executes recurring jobs in the
        // same process; split to a dedicated worker in a future scale-out.
        builder.Services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(opt =>
                opt.UseNpgsqlConnection(connectionString)));

        builder.Services.AddHangfireServer(options =>
        {
            // Keep worker count low — our jobs are lightweight DB operations,
            // not CPU-bound.  Raise if the queue starts backing up.
            options.WorkerCount = 4;
            options.ServerName = $"kraken:{Environment.MachineName}";
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

        // ── Build & configure pipeline ────────────────────────────────────────
        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
            await db.Database.MigrateAsync().ConfigureAwait(false);

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

        app.UseHttpsRedirection();

        // Serilog request logging — writes one structured log line per HTTP request.
        // Must come before auth middleware so it captures the full request duration.
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "HTTP {RequestMethod} {RequestPath} responded {StatusCode} " +
                "in {Elapsed:0.0} ms";
        });

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

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
            IAuthenticationSchemeProvider schemeProvider) =>
        {
            var scheme = await schemeProvider.GetSchemeAsync(provider);
            if (scheme is null || !provider.StartsWith("oidc_", StringComparison.Ordinal))
            {
                return Results.Redirect("/login?error=unknown_provider");
            }

            var safeReturn = !string.IsNullOrEmpty(returnUrl)
                && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
                ? returnUrl : "/";

            var props = new AuthenticationProperties { RedirectUri = safeReturn };
            await http.ChallengeAsync(provider, props).ConfigureAwait(false);
            return Results.Empty;
        }).AllowAnonymous();

        app.MapHub<AgentHub>("/hubs/agent");
        app.MapHub<UiHub>("/hubs/ui");
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
            }).AllowAnonymous();

        // ── Agent REST API (non-SignalR transports: Direct, Polling) ──────────────
        // Mirrors the SignalR hub methods so DirectServerLink and PollingServerLink
        // can communicate with the server via plain HTTP.
        //
        // Heartbeat: POST /api/agents/heartbeat
        app.MapPost("/api/agents/heartbeat",
            async (
                HeartbeatRequest req,
                KrakenDbContext db,
                TargetStatusPublisher statusPub,
                TimeProvider timeProvider,
                HttpContext http,
                CancellationToken ct) =>
            {
                var targetIdClaim = http.User.FindFirst(
                    System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (targetIdClaim is null || !Guid.TryParse(targetIdClaim, out var targetId))
                {
                    return Results.Unauthorized();
                }

                var target = await db.DeploymentTargets
                    .FindAsync(new object[] { targetId }, ct)
                    .ConfigureAwait(false);
                if (target is null)
                {
                    return Results.NotFound();
                }

                var now = timeProvider.GetUtcNow();
                target.LastSeenUtc = now;
                if (req.MachineName is not null)
                {
                    target.MachineName = req.MachineName;
                }

                if (req.OperatingSystem is not null)
                {
                    target.OperatingSystem = req.OperatingSystem;
                }

                if (req.AgentVersion is not null)
                {
                    target.AgentVersion = req.AgentVersion;
                }
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                // If the target was offline, transition to Online.
                if (target.Status != TargetStatus.Online)
                {
                    target.Status = TargetStatus.Online;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await statusPub.PublishAsync(targetId, TargetStatus.Online, now)
                        .ConfigureAwait(false);
                }

                return Results.NoContent();
            }).RequireAuthorization("AgentJwt");

        // Report status: POST /api/agents/status
        app.MapPost("/api/agents/status",
            async (
                HttpContext http,
                KrakenDbContext db,
                CancellationToken ct) =>
            {
                var targetIdClaim = http.User.FindFirst(
                    System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (targetIdClaim is null || !Guid.TryParse(targetIdClaim, out var targetId))
                {
                    return Results.Unauthorized();
                }

                // Read the status string from the request body.
                string status;
                using (var reader = new System.IO.StreamReader(http.Request.Body))
                {
                    status = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                }

                var target = await db.DeploymentTargets
                    .FindAsync(new object[] { targetId }, ct)
                    .ConfigureAwait(false);
                if (target is null)
                {
                    return Results.NotFound();
                }

                if (status.Contains("ShuttingDown", StringComparison.OrdinalIgnoreCase))
                {
                    target.Status = TargetStatus.Offline;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                return Results.NoContent();
            }).RequireAuthorization("AgentJwt");

        // Append a log line: POST /api/deployments/{id:guid}/logs
        app.MapPost("/api/deployments/{id:guid}/logs",
            async (
                Guid id,
                DeploymentLogLineRequest req,
                KrakenDbContext db,
                TimeProvider timeProvider,
                CancellationToken ct) =>
            {
                var deployment = await db.Deployments.FindAsync(new object[] { id }, ct)
                    .ConfigureAwait(false);
                if (deployment is null)
                {
                    // Try runbook runs.
                    var run = await db.RunbookRuns.FindAsync(new object[] { id }, ct)
                        .ConfigureAwait(false);
                    if (run is null)
                    {
                        return Results.NotFound();
                    }

                    var runEntry = new KrakenDeploy.Server.Core.Domain.Runbooks.RunbookRunLogEntry
                    {
                        RunbookRunId = id,
                        Level = req.Level,
                        Message = req.Message,
                        Timestamp = timeProvider.GetUtcNow(),
                    };
                    db.RunbookRunLogEntries.Add(runEntry);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return Results.NoContent();
                }

                var entry = new KrakenDeploy.Server.Core.Domain.Deployments.DeploymentLogEntry
                {
                    DeploymentId = id,
                    Level = req.Level,
                    Message = req.Message,
                    Timestamp = timeProvider.GetUtcNow(),
                };
                db.DeploymentLogEntries.Add(entry);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Results.NoContent();
            }).RequireAuthorization("AgentJwt");

        // Complete a deployment: POST /api/deployments/{id:guid}/complete
        app.MapPost("/api/deployments/{id:guid}/complete",
            async (
                Guid id,
                CompleteDeploymentRequest req,
                KrakenDbContext db,
                TimeProvider timeProvider,
                TargetStatusPublisher statusPub,
                CancellationToken ct) =>
            {
                var deployment = await db.Deployments.FindAsync(new object[] { id }, ct)
                    .ConfigureAwait(false);
                if (deployment is null)
                {
                    // Try runbook runs.
                    var run = await db.RunbookRuns.FindAsync(new object[] { id }, ct)
                        .ConfigureAwait(false);
                    if (run is null)
                    {
                        return Results.NotFound();
                    }

                    run.Status = req.Success
                        ? KrakenDeploy.Server.Core.Domain.Deployments.DeploymentStatus.Succeeded
                        : KrakenDeploy.Server.Core.Domain.Deployments.DeploymentStatus.Failed;
                    run.CompletedUtc = timeProvider.GetUtcNow();
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return Results.NoContent();
                }

                deployment.Status = req.Success
                    ? KrakenDeploy.Server.Core.Domain.Deployments.DeploymentStatus.Succeeded
                    : KrakenDeploy.Server.Core.Domain.Deployments.DeploymentStatus.Failed;
                deployment.CompletedUtc = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Results.NoContent();
            }).RequireAuthorization("AgentJwt");

        // Pending work (polling mode): GET /api/agents/pending-work/{targetId:guid}
        app.MapGet("/api/agents/pending-work/{targetId:guid}",
            async (
                Guid targetId,
                KrakenDbContext db,
                VariableService variableSvc,
                CancellationToken ct) =>
            {
                // Find the next Queued deployment for this target.
                var deployment = await db.Deployments
                    .Where(d => d.TargetId == targetId
                        && d.Status == KrakenDeploy.Server.Core.Domain.Deployments.DeploymentStatus.Queued)
                    .OrderBy(d => d.CreatedUtc)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);

                if (deployment is null)
                {
                    return Results.Ok(new { pending = false });
                }

                var release = await db.Releases
                    .Include(r => r.ProcessSnapshot)
                    .FirstOrDefaultAsync(r => r.Id == deployment.ReleaseId, ct)
                    .ConfigureAwait(false);

                if (release is null)
                {
                    return Results.Ok(new { pending = false });
                }

                // Build the deployment plan (same as DeploymentWorker)
                var project = await db.Projects
                    .FindAsync(new object[] { release.ProjectId }, ct)
                    .ConfigureAwait(false);
                var env = await db.Environments
                    .FindAsync(new object[] { deployment.EnvironmentId }, ct)
                    .ConfigureAwait(false);

                var target = await db.DeploymentTargets
                    .FindAsync(new object[] { deployment.TargetId!.Value }, ct)
                    .ConfigureAwait(false);

                var variables = await variableSvc
                    .ResolveAsync(release.ProjectId, deployment.EnvironmentId,
                        deployment.TargetId!.Value, target?.Roles ?? [],
                        deployment.TenantId, ct)
                    .ConfigureAwait(false);

                // Split resolved variables into scalar and array.
                // StringArray values from ResolveAsync are JSON arrays like ["a","b"].
                var flatVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var arrayVars = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

                foreach (var (name, value) in variables)
                {
                    try
                    {
                        var items = System.Text.Json.JsonSerializer.Deserialize<string[]>(value);
                        if (items is not null)
                        {
                            arrayVars[name] = items;
                            flatVars[name] = string.Join(", ", items);
                            continue;
                        }
                    }
                    catch (System.Text.Json.JsonException) { }

                    flatVars[name] = value;
                }

                var steps = release.ProcessSnapshot
                    .OrderBy(s => s.SortOrder)
                    .Select(s => new KrakenDeploy.Contracts.DeploymentStepPlan(
                        Index: s.SortOrder,
                        Name: s.Name,
                        StepType: s.StepType,
                        PackageId: s.PackageId ?? "",
                        PackageVersion: s.PackageVersion ?? "",
                        Config: s.Config ?? new Dictionary<string, string>(StringComparer.Ordinal)))
                    .ToArray();

                var plan = new KrakenDeploy.Contracts.DeploymentPlan(
                    DeploymentId: deployment.Id,
                    EnvironmentName: env?.Name ?? "",
                    Steps: steps,
                    Variables: flatVars,
                    ArrayVariables: arrayVars);

                return Results.Ok(new { pending = true, plan });
            }).RequireAuthorization("AgentJwt");

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
            }).RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName, "AgentJwt");

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
            }).RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName, "AgentJwt");

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
            async (SpaceService spaceSvc, CancellationToken ct) =>
                Results.Ok(await spaceSvc.GetAllAsync(ct).ConfigureAwait(false))
        ).RequireAuthorization(); // List of accessible Spaces — any authenticated user
                                  // can see the Spaces they belong to (filtered server-side
                                  // when membership lands; for now: all Spaces).

        // Switch the active Space — sets the kraken-active-space cookie. Two
        // entry points:
        //   POST /api/spaces/switch/{id} — for programmatic / API clients
        //   GET  /space/switch?id={id}    — for the Blazor SpaceSwitcher
        //                                   (browser nav with full reload so
        //                                    DI scopes pick up the new context)
        static async Task<IResult> SwitchSpaceAsync(
            Guid spaceId,
            string? returnUrl,
            SpaceService spaceSvc,
            HttpContext http,
            CancellationToken ct,
            bool redirect)
        {
            // Validate the Space exists. Membership / authorization checks
            // come with the M10 Permission/Role/Team model — for now any
            // authenticated user can switch to any Space.
            var space = await spaceSvc.GetAsync(spaceId, ct).ConfigureAwait(false);
            if (space is null)
            {
                return Results.NotFound(new { error = "Space not found." });
            }

            if (space.Status != SpaceStatus.Active)
            {
                return Results.BadRequest(new { error = $"Space is {space.Status} and cannot be selected." });
            }

            http.Response.Cookies.Append(
                HttpSpaceContext.ActiveSpaceCookieName,
                spaceId.ToString(),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure   = http.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Path     = "/",
                    Expires  = DateTimeOffset.UtcNow.AddDays(365),
                });

            if (redirect)
            {
                // Only allow same-origin returnUrls — never trust user-supplied
                // absolute URLs (open-redirect attack).
                var safe = !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/')
                    ? returnUrl : "/";
                return Results.Redirect(safe);
            }

            return Results.Ok(new { spaceId, space.Slug, space.Name });
        }

        app.MapPost("/api/spaces/switch/{spaceId:guid}",
            (Guid spaceId, SpaceService spaceSvc, HttpContext http, CancellationToken ct)
                => SwitchSpaceAsync(spaceId, returnUrl: null, spaceSvc, http, ct, redirect: false))
            .RequireAuthorization(); // Selecting a space is an identity action,
                                     // not a Permission — gated by membership when that lands.

        app.MapGet("/space/switch",
            (Guid id, string? returnUrl, SpaceService spaceSvc, HttpContext http, CancellationToken ct)
                => SwitchSpaceAsync(id, returnUrl, spaceSvc, http, ct, redirect: true))
            .RequireAuthorization();

        app.MapPost("/api/spaces",
            async (CreateSpaceRequest req, SpaceService spaceSvc, CancellationToken ct) =>
            {
                try
                {
                    var space = await spaceSvc.CreateAsync(req.Slug, req.Name, req.Description, ct)
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
                    req.StepPackageName, req.StepPackageVersion, ct).ConfigureAwait(false);
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
                Results.Ok(await deploymentSvc.GetAllAsync(projectId, ct).ConfigureAwait(false))
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
                            req.ReleaseId, req.EnvironmentId, req.TargetId,
                            req.TenantId, req.ScheduledFor, ct)
                        .ConfigureAwait(false);
                    return Results.Created($"/api/deployments/{deployment.Id}", deployment);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.DeploymentCreate);

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
                return Results.Ok(new { hmacKeyGenerated = true });
            }).RequirePermission(Permission.MachineEdit);

        // ── Tenant API ─────────────────────────────────────────────────────────────

        app.MapGet("/api/tenants",
            async (TenantService tenantSvc, CancellationToken ct) =>
                Results.Ok(await tenantSvc.GetAllAsync(ct).ConfigureAwait(false)))
            .RequirePermission(Permission.TenantView);

        app.MapGet("/api/tenants/{id:guid}",
            async (Guid id, TenantService tenantSvc, CancellationToken ct) =>
            {
                var tenant = await tenantSvc.GetWithTagsAsync(id, ct).ConfigureAwait(false);
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

        // Tag Sets
        app.MapGet("/api/tenants/{tenantId:guid}/tag-sets",
            async (Guid tenantId, TenantService tenantSvc, CancellationToken ct) =>
                Results.Ok(await tenantSvc.GetTagSetsAsync(tenantId, ct).ConfigureAwait(false)))
            .RequirePermission(Permission.TagSetView);

        app.MapPost("/api/tenants/{tenantId:guid}/tag-sets",
            async (Guid tenantId, CreateTagSetRequest req, TenantService tenantSvc, CancellationToken ct) =>
            {
                try
                {
                    var ts = await tenantSvc.CreateTagSetAsync(tenantId, req.Name, req.Description, req.SortOrder, ct)
                        .ConfigureAwait(false);
                    return Results.Created($"/api/tenants/{tenantId}/tag-sets/{ts.Id}", ts);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.TagSetCreate);

        app.MapPut("/api/tag-sets/{id:guid}",
            async (Guid id, CreateTagSetRequest req, TenantService tenantSvc, CancellationToken ct) =>
            {
                var ts = await tenantSvc.UpdateTagSetAsync(id, req.Name, req.Description, req.SortOrder, ct)
                    .ConfigureAwait(false);
                return ts is null ? Results.NotFound() : Results.Ok(ts);
            }).RequirePermission(Permission.TagSetEdit);

        app.MapDelete("/api/tag-sets/{id:guid}",
            async (Guid id, TenantService tenantSvc, CancellationToken ct) =>
            {
                var deleted = await tenantSvc.DeleteTagSetAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequirePermission(Permission.TagSetDelete);

        // Tags
        app.MapPost("/api/tag-sets/{tagSetId:guid}/tags",
            async (Guid tagSetId, CreateTenantTagRequest req, TenantService tenantSvc, CancellationToken ct) =>
            {
                try
                {
                    var tag = await tenantSvc.CreateTagAsync(tagSetId, req.Name, req.Color, ct)
                        .ConfigureAwait(false);
                    return Results.Created($"/api/tag-sets/{tagSetId}/tags/{tag.Id}", tag);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.TagSetEdit);

        app.MapPut("/api/tags/{id:guid}",
            async (Guid id, CreateTenantTagRequest req, TenantService tenantSvc, CancellationToken ct) =>
            {
                var tag = await tenantSvc.UpdateTagAsync(id, req.Name, req.Color, ct)
                    .ConfigureAwait(false);
                return tag is null ? Results.NotFound() : Results.Ok(tag);
            }).RequirePermission(Permission.TagSetEdit);

        app.MapDelete("/api/tags/{id:guid}",
            async (Guid id, TenantService tenantSvc, CancellationToken ct) =>
            {
                var deleted = await tenantSvc.DeleteTagAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequirePermission(Permission.TagSetEdit);

        // Target-Tag connections
        app.MapPost("/api/tags/{tagId:guid}/targets/{targetId:guid}",
            async (Guid tagId, Guid targetId, TenantService tenantSvc, CancellationToken ct) =>
            {
                try
                {
                    await tenantSvc.AddTagToTargetAsync(tagId, targetId, ct).ConfigureAwait(false);
                    return Results.NoContent();
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequirePermission(Permission.MachineEdit);

        app.MapDelete("/api/tags/{tagId:guid}/targets/{targetId:guid}",
            async (Guid tagId, Guid targetId, TenantService tenantSvc, CancellationToken ct) =>
            {
                await tenantSvc.RemoveTagFromTargetAsync(tagId, targetId, ct).ConfigureAwait(false);
                return Results.NoContent();
            }).RequirePermission(Permission.MachineEdit);

        app.MapGet("/api/targets/{targetId:guid}/tags",
            async (Guid targetId, TenantService tenantSvc, CancellationToken ct) =>
                Results.Ok(await tenantSvc.GetTagsForTargetAsync(targetId, ct).ConfigureAwait(false)))
            .RequirePermission(Permission.MachineView);

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
                    req.StepPackageName, req.StepPackageVersion, ct)
                    .ConfigureAwait(false);
                return Results.Created($"/api/runbooks/{runbookId}/steps/{step.Id}", step);
            }).RequirePermission(Permission.RunbookEdit);

        app.MapPut("/api/runbook-steps/{stepId:guid}",
            async (Guid stepId, AddStepRequest req, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var step = await runbookSvc.UpdateStepAsync(
                    stepId, req.Name, req.StepType, req.PackageId, req.TargetRoles, req.Config,
                    req.StepPackageName, req.StepPackageVersion, ct)
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
                    var (_, token) = await registrationSvc
                        .CreateAsync("smoke-agent", ["smoke"], TransportMode.Reverse, ct)
                        .ConfigureAwait(false);
                    return Results.Ok(new { token });
                }).AllowAnonymous();
        }

        // Register Hangfire recurring jobs after the app is built so the storage
        // is fully initialised.  Safe to call multiple times (AddOrUpdate is
        // idempotent) — on each restart the schedule is refreshed.
        HangfireJobRegistrar.RegisterRecurringJobs();

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
}
