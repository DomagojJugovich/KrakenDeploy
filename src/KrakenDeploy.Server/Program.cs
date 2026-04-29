using System.Globalization;
using System.Text;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Commands;
using KrakenDeploy.Server.Components;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Services;
using KrakenDeploy.Server.Transport;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Variables;
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
        if (args.Length > 0 && args[0] == "users")
        {
            return await UserCommands.RunAsync(args.AsSpan(1).ToArray()).ConfigureAwait(false);
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
        builder.Services.AddKrakenDeployIdentityCore();

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

        // Agent JWT bearer — separate scheme so it doesn't conflict with the
        // cookie auth used by the Blazor UI.
        var agentJwtKey = builder.Configuration["Agent:JwtSigningKey"];
        if (string.IsNullOrWhiteSpace(agentJwtKey))
        {
            throw new InvalidOperationException(
                "Agent:JwtSigningKey is not configured. " +
                "Set it in appsettings or user-secrets (minimum 32 characters for HS256).");
        }

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

        builder.Services.AddSingleton<IAgentConnectionRegistry, InMemoryAgentConnectionRegistry>();
        builder.Services.AddSingleton<AgentJwtService>();
        builder.Services.AddSingleton<ITargetStatusNotifier, InMemoryTargetStatusNotifier>();
        builder.Services.AddSingleton<TargetStatusPublisher>();
        builder.Services.AddHostedService<DeploymentWorker>();

        // ── Authorization ────────────────────────────────────────────────────
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

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

        // ── Blazor UI ────────────────────────────────────────────────────────
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddRadzenComponents();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // ── Build & configure pipeline ────────────────────────────────────────
        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
            await db.Database.MigrateAsync().ConfigureAwait(false);
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

        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.MapPost("/logout", async (SignInManager<ApplicationUser> signInManager) =>
        {
            await signInManager.SignOutAsync().ConfigureAwait(false);
            return Results.Redirect("/login");
        }).RequireAuthorization();

        app.MapHub<AgentHub>("/hubs/agent");
        app.MapHub<UiHub>("/hubs/ui");
        app.MapGrpcService<GrpcPackageDeliveryService>();

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
                return Results.Ok(new RegisterAgentResponse(target.Id, jwt));
            }).AllowAnonymous();

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
            }).RequireAuthorization();

        app.MapGet("/api/packages",
            async (PackageService packageSvc, CancellationToken ct) =>
                Results.Ok(await packageSvc.GetSummariesAsync(ct).ConfigureAwait(false))
        ).RequireAuthorization();

        app.MapGet("/api/packages/{packageId}/versions",
            async (string packageId, PackageService packageSvc, CancellationToken ct) =>
                Results.Ok(await packageSvc.GetVersionsAsync(packageId, ct).ConfigureAwait(false))
        ).RequireAuthorization();

        app.MapDelete("/api/packages/{id:guid}",
            async (Guid id, PackageService packageSvc, CancellationToken ct) =>
            {
                var deleted = await packageSvc.DeleteAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequireAuthorization();

        // ── Process API ──────────────────────────────────────────────────────
        app.MapGet("/api/projects/{projectId:guid}/process",
            async (Guid projectId, ProcessService processSvc, CancellationToken ct) =>
            {
                var process = await processSvc.GetAsync(projectId, ct).ConfigureAwait(false);
                return process is null ? Results.NotFound() : Results.Ok(process);
            }).RequireAuthorization();

        app.MapPost("/api/projects/{projectId:guid}/process/steps",
            async (Guid projectId, AddStepRequest req, ProcessService processSvc, CancellationToken ct) =>
            {
                var step = await processSvc.AddStepAsync(
                    projectId, req.Name, req.StepType, req.PackageId,
                    req.TargetRoles, req.Config, ct).ConfigureAwait(false);
                return Results.Created($"/api/projects/{projectId}/process/steps/{step.Id}", step);
            }).RequireAuthorization();

        app.MapDelete("/api/projects/{projectId:guid}/process/steps/{stepId:guid}",
            async (Guid projectId, Guid stepId, ProcessService processSvc, CancellationToken ct) =>
            {
                var removed = await processSvc.RemoveStepAsync(stepId, ct).ConfigureAwait(false);
                return removed ? Results.NoContent() : Results.NotFound();
            }).RequireAuthorization();

        // ── Release API ──────────────────────────────────────────────────────
        app.MapGet("/api/projects/{projectId:guid}/releases",
            async (Guid projectId, ReleaseService releaseSvc, CancellationToken ct) =>
                Results.Ok(await releaseSvc.GetAllAsync(projectId, ct).ConfigureAwait(false))
        ).RequireAuthorization();

        app.MapPost("/api/projects/{projectId:guid}/releases",
            async (Guid projectId, CreateReleaseRequest req, ReleaseService releaseSvc,
                CancellationToken ct) =>
            {
                try
                {
                    var release = await releaseSvc.CreateAsync(
                        projectId, req.Version, req.PackageVersions, req.ReleaseNotes, ct)
                        .ConfigureAwait(false);
                    return Results.Created(
                        $"/api/projects/{projectId}/releases/{release.Id}", release);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(new { error = ex.Message });
                }
            }).RequireAuthorization();

        // ── Variable API ─────────────────────────────────────────────────────
        app.MapGet("/api/projects/{projectId:guid}/variables",
            async (Guid projectId, VariableService variableSvc, CancellationToken ct) =>
                Results.Ok(await variableSvc.GetVariablesAsync(projectId, ct).ConfigureAwait(false))
        ).RequireAuthorization();

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
            }).RequireAuthorization();

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
                    EnvironmentId = req.ScopeEnvironmentId,
                    TargetId = req.ScopeTargetId,
                    Roles = req.ScopeRoles,
                };

                var variable = await variableSvc
                    .UpdateVariableAsync(variableId, req.Name, req.Value, type, scope, ct)
                    .ConfigureAwait(false);

                return variable is null ? Results.NotFound() : Results.Ok(variable);
            }).RequireAuthorization();

        app.MapDelete("/api/projects/{projectId:guid}/variables/{variableId:guid}",
            async (Guid projectId, Guid variableId, VariableService variableSvc, CancellationToken ct) =>
            {
                var deleted = await variableSvc.DeleteVariableAsync(variableId, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequireAuthorization();

        // ── Deployment API ───────────────────────────────────────────────────
        app.MapGet("/api/deployments",
            async (Guid? projectId, DeploymentService deploymentSvc, CancellationToken ct) =>
                Results.Ok(await deploymentSvc.GetAllAsync(projectId, ct).ConfigureAwait(false))
        ).RequireAuthorization();

        app.MapGet("/api/deployments/{id:guid}",
            async (Guid id, DeploymentService deploymentSvc, CancellationToken ct) =>
            {
                var d = await deploymentSvc.GetAsync(id, ct).ConfigureAwait(false);
                return d is null ? Results.NotFound() : Results.Ok(d);
            }).RequireAuthorization();

        app.MapPost("/api/deployments",
            async (TriggerDeploymentRequest req, DeploymentService deploymentSvc,
                CancellationToken ct) =>
            {
                try
                {
                    var deployment = await deploymentSvc
                        .CreateAsync(req.ReleaseId, req.EnvironmentId, req.TargetId, ct)
                        .ConfigureAwait(false);
                    return Results.Created($"/api/deployments/{deployment.Id}", deployment);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequireAuthorization();

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
}
