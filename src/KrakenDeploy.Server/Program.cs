using System.Globalization;
using System.Text;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Auth;
using KrakenDeploy.Server.Commands;
using KrakenDeploy.Server.Components;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Services;
using KrakenDeploy.Server.Transport;
using KrakenDeploy.Server.Core.Domain.Lifecycles;
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

        builder.Services.AddSingleton<IAgentConnectionRegistry, InMemoryAgentConnectionRegistry>();
        builder.Services.AddSingleton<AgentJwtService>();
        builder.Services.AddSingleton<ITargetStatusNotifier, InMemoryTargetStatusNotifier>();
        builder.Services.AddSingleton<TargetStatusPublisher>();
        builder.Services.AddHostedService<DeploymentWorker>();
        builder.Services.AddHostedService<RunbookRunWorker>();

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

            // Defensive: ensure the Default Space exists. The AddSpacesFoundation
            // migration seeds it, but a fresh DB created by `EnsureCreated()` (or
            // a corrupted seed) would leave us without one.
            var spaceService = scope.ServiceProvider.GetRequiredService<SpaceService>();
            await spaceService.EnsureDefaultAsync().ConfigureAwait(false);

            // Seed built-in step templates (Kraken.IIS, etc.). Idempotent.
            var seeder = scope.ServiceProvider.GetRequiredService<BuiltInStepTemplateSeeder>();
            await seeder.SeedAsync().ConfigureAwait(false);

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

        // ── Project API (CLI / REST) ─────────────────────────────────────────
        // ── Spaces API ──────────────────────────────────────────────────────────
        app.MapGet("/api/spaces",
            async (SpaceService spaceSvc, CancellationToken ct) =>
                Results.Ok(await spaceSvc.GetAllAsync(ct).ConfigureAwait(false))
        ).RequireAuthorization();

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
            .RequireAuthorization();

        app.MapGet("/space/switch",
            (Guid id, string? returnUrl, SpaceService spaceSvc, HttpContext http, CancellationToken ct)
                => SwitchSpaceAsync(id, returnUrl, spaceSvc, http, ct, redirect: true))
            .RequireAuthorization();

        app.MapGet("/api/projects",
            async (ProjectService projectSvc, CancellationToken ct) =>
                Results.Ok(await projectSvc.GetAllAsync(ct).ConfigureAwait(false))
        ).RequireAuthorization();

        app.MapGet("/api/projects/{id:guid}",
            async (Guid id, ProjectService projectSvc, CancellationToken ct) =>
            {
                var project = await projectSvc.GetAsync(id, ct).ConfigureAwait(false);
                return project is null ? Results.NotFound() : Results.Ok(project);
            }).RequireAuthorization();

        app.MapGet("/api/projects/by-slug/{slug}",
            async (string slug, ProjectService projectSvc, CancellationToken ct) =>
            {
                var project = await projectSvc.GetBySlugAsync(slug, ct).ConfigureAwait(false);
                return project is null ? Results.NotFound() : Results.Ok(project);
            }).RequireAuthorization();

        // ── Environment API (CLI / REST) ─────────────────────────────────────
        app.MapGet("/api/environments",
            async (EnvironmentService envSvc, CancellationToken ct) =>
                Results.Ok(await envSvc.GetAllOrderedAsync(ct).ConfigureAwait(false))
        ).RequireAuthorization();

        // ── Target API (CLI / REST) ──────────────────────────────────────────
        app.MapGet("/api/targets",
            async (TargetService targetSvc, CancellationToken ct) =>
                Results.Ok(await targetSvc.GetAllAsync(ct).ConfigureAwait(false))
        ).RequireAuthorization();

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
                        projectId, req.Version, req.PackageVersions, req.ReleaseNotes, req.ChannelId, ct)
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
                    TenantId = req.ScopeTenantId,
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

        // ── Step-template API ────────────────────────────────────────────────
        app.MapGet("/api/step-templates",
            async (StepTemplateService svc, CancellationToken ct) =>
            {
                var templates = await svc.GetAllAsync(ct).ConfigureAwait(false);
                var summaries = templates.Select(t => new StepTemplateSummaryDto(
                    t.Id, t.Name, t.Description, t.ActionType,
                    t.Parameters.Count, t.Version, t.CreatedUtc));
                return Results.Ok(summaries);
            }).RequireAuthorization();

        app.MapGet("/api/step-templates/{id:guid}",
            async (Guid id, StepTemplateService svc, CancellationToken ct) =>
            {
                var template = await svc.GetAsync(id, ct).ConfigureAwait(false);
                return template is null ? Results.NotFound() : Results.Ok(template);
            }).RequireAuthorization();

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
            }).RequireAuthorization();

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
            }).RequireAuthorization();

        app.MapDelete("/api/step-templates/{id:guid}",
            async (Guid id, StepTemplateService svc, CancellationToken ct) =>
            {
                var deleted = await svc.DeleteAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequireAuthorization();

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
                        req.Json, req.ImportSource, ct)
                        .ConfigureAwait(false);
                    return Results.Ok(template);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
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
            }).RequireAuthorization();

        app.MapPost("/api/deployments",
            async (TriggerDeploymentRequest req, DeploymentService deploymentSvc,
                CancellationToken ct) =>
            {
                try
                {
                    var deployment = await deploymentSvc
                        .CreateAsync(req.ReleaseId, req.EnvironmentId, req.TargetId, req.TenantId, ct)
                        .ConfigureAwait(false);
                    return Results.Created($"/api/deployments/{deployment.Id}", deployment);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }).RequireAuthorization();

        // ── Artifact API ─────────────────────────────────────────────────────────
        app.MapGet("/api/deployments/{id:guid}/artifacts",
            async (Guid id, ArtifactService artifactSvc, CancellationToken ct) =>
                Results.Ok(await artifactSvc.GetByDeploymentAsync(id, ct).ConfigureAwait(false))
        ).RequireAuthorization();

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
            }).RequireAuthorization();

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
            }).RequireAuthorization();

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
            }).RequireAuthorization().DisableAntiforgery();

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
            }).RequireAuthorization();

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
            }).RequireAuthorization();

        // ── Tenant API ─────────────────────────────────────────────────────────────

        app.MapGet("/api/tenants",
            async (TenantService tenantSvc, CancellationToken ct) =>
                Results.Ok(await tenantSvc.GetAllAsync(ct).ConfigureAwait(false)))
            .RequireAuthorization();

        app.MapGet("/api/tenants/{id:guid}",
            async (Guid id, TenantService tenantSvc, CancellationToken ct) =>
            {
                var tenant = await tenantSvc.GetWithTagsAsync(id, ct).ConfigureAwait(false);
                return tenant is null ? Results.NotFound() : Results.Ok(tenant);
            }).RequireAuthorization();

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
            }).RequireAuthorization();

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
            }).RequireAuthorization();

        app.MapDelete("/api/tenants/{id:guid}",
            async (Guid id, TenantService tenantSvc, CancellationToken ct) =>
            {
                var deleted = await tenantSvc.DeleteAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequireAuthorization();

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
            }).RequireAuthorization();

        app.MapDelete("/api/tenants/{tenantId:guid}/projects/{projectId:guid}",
            async (Guid tenantId, Guid projectId, TenantService tenantSvc, CancellationToken ct) =>
            {
                await tenantSvc.DisconnectProjectAsync(tenantId, projectId, ct).ConfigureAwait(false);
                return Results.NoContent();
            }).RequireAuthorization();

        // Tag Sets
        app.MapGet("/api/tenants/{tenantId:guid}/tag-sets",
            async (Guid tenantId, TenantService tenantSvc, CancellationToken ct) =>
                Results.Ok(await tenantSvc.GetTagSetsAsync(tenantId, ct).ConfigureAwait(false)))
            .RequireAuthorization();

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
            }).RequireAuthorization();

        app.MapPut("/api/tag-sets/{id:guid}",
            async (Guid id, CreateTagSetRequest req, TenantService tenantSvc, CancellationToken ct) =>
            {
                var ts = await tenantSvc.UpdateTagSetAsync(id, req.Name, req.Description, req.SortOrder, ct)
                    .ConfigureAwait(false);
                return ts is null ? Results.NotFound() : Results.Ok(ts);
            }).RequireAuthorization();

        app.MapDelete("/api/tag-sets/{id:guid}",
            async (Guid id, TenantService tenantSvc, CancellationToken ct) =>
            {
                var deleted = await tenantSvc.DeleteTagSetAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequireAuthorization();

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
            }).RequireAuthorization();

        app.MapPut("/api/tags/{id:guid}",
            async (Guid id, CreateTenantTagRequest req, TenantService tenantSvc, CancellationToken ct) =>
            {
                var tag = await tenantSvc.UpdateTagAsync(id, req.Name, req.Color, ct)
                    .ConfigureAwait(false);
                return tag is null ? Results.NotFound() : Results.Ok(tag);
            }).RequireAuthorization();

        app.MapDelete("/api/tags/{id:guid}",
            async (Guid id, TenantService tenantSvc, CancellationToken ct) =>
            {
                var deleted = await tenantSvc.DeleteTagAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequireAuthorization();

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
            }).RequireAuthorization();

        app.MapDelete("/api/tags/{tagId:guid}/targets/{targetId:guid}",
            async (Guid tagId, Guid targetId, TenantService tenantSvc, CancellationToken ct) =>
            {
                await tenantSvc.RemoveTagFromTargetAsync(tagId, targetId, ct).ConfigureAwait(false);
                return Results.NoContent();
            }).RequireAuthorization();

        app.MapGet("/api/targets/{targetId:guid}/tags",
            async (Guid targetId, TenantService tenantSvc, CancellationToken ct) =>
                Results.Ok(await tenantSvc.GetTagsForTargetAsync(targetId, ct).ConfigureAwait(false)))
            .RequireAuthorization();

        // ── Lifecycle API ──────────────────────────────────────────────────────────

        app.MapGet("/api/lifecycles",
            async (LifecycleService lcSvc, CancellationToken ct) =>
                Results.Ok(await lcSvc.GetAllAsync(ct).ConfigureAwait(false)))
            .RequireAuthorization();

        app.MapGet("/api/lifecycles/{id:guid}",
            async (Guid id, LifecycleService lcSvc, CancellationToken ct) =>
            {
                var lc = await lcSvc.GetAsync(id, ct).ConfigureAwait(false);
                return lc is null ? Results.NotFound() : Results.Ok(lc);
            }).RequireAuthorization();

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
            }).RequireAuthorization();

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
            }).RequireAuthorization();

        app.MapDelete("/api/lifecycles/{id:guid}",
            async (Guid id, LifecycleService lcSvc, CancellationToken ct) =>
            {
                var deleted = await lcSvc.DeleteAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequireAuthorization();

        // ── Channel API ────────────────────────────────────────────────────────────

        app.MapGet("/api/projects/{projectId:guid}/channels",
            async (Guid projectId, ChannelService channelSvc, CancellationToken ct) =>
                Results.Ok(await channelSvc.GetForProjectAsync(projectId, ct).ConfigureAwait(false)))
            .RequireAuthorization();

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
            }).RequireAuthorization();

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
            }).RequireAuthorization();

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
            }).RequireAuthorization();

        // ── Runbook API ────────────────────────────────────────────────────────────

        app.MapGet("/api/projects/{projectId:guid}/runbooks",
            async (Guid projectId, RunbookService runbookSvc, CancellationToken ct) =>
                Results.Ok(await runbookSvc.GetAllAsync(projectId, ct).ConfigureAwait(false)))
            .RequireAuthorization();

        app.MapGet("/api/runbooks/{id:guid}",
            async (Guid id, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var rb = await runbookSvc.GetAsync(id, ct).ConfigureAwait(false);
                return rb is null ? Results.NotFound() : Results.Ok(rb);
            }).RequireAuthorization();

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
            }).RequireAuthorization();

        app.MapPut("/api/runbooks/{id:guid}",
            async (Guid id, CreateRunbookRequest req, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var rb = await runbookSvc.UpdateAsync(id, req.Name, req.Description, ct)
                    .ConfigureAwait(false);
                return rb is null ? Results.NotFound() : Results.Ok(rb);
            }).RequireAuthorization();

        app.MapDelete("/api/runbooks/{id:guid}",
            async (Guid id, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var deleted = await runbookSvc.DeleteAsync(id, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequireAuthorization();

        // Runbook steps
        app.MapPost("/api/runbooks/{runbookId:guid}/steps",
            async (Guid runbookId, AddStepRequest req, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var step = await runbookSvc.AddStepAsync(
                    runbookId, req.Name, req.StepType, req.PackageId, req.TargetRoles, req.Config, ct)
                    .ConfigureAwait(false);
                return Results.Created($"/api/runbooks/{runbookId}/steps/{step.Id}", step);
            }).RequireAuthorization();

        app.MapPut("/api/runbook-steps/{stepId:guid}",
            async (Guid stepId, AddStepRequest req, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var step = await runbookSvc.UpdateStepAsync(
                    stepId, req.Name, req.StepType, req.PackageId, req.TargetRoles, req.Config, ct)
                    .ConfigureAwait(false);
                return step is null ? Results.NotFound() : Results.Ok(step);
            }).RequireAuthorization();

        app.MapDelete("/api/runbook-steps/{stepId:guid}",
            async (Guid stepId, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var deleted = await runbookSvc.DeleteStepAsync(stepId, ct).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            }).RequireAuthorization();

        // Runbook runs
        app.MapGet("/api/runbooks/{runbookId:guid}/runs",
            async (Guid runbookId, RunbookService runbookSvc, CancellationToken ct) =>
                Results.Ok(await runbookSvc.GetRunsAsync(runbookId, ct).ConfigureAwait(false)))
            .RequireAuthorization();

        app.MapGet("/api/runbook-runs/{runId:guid}",
            async (Guid runId, RunbookService runbookSvc, CancellationToken ct) =>
            {
                var run = await runbookSvc.GetRunAsync(runId, ct).ConfigureAwait(false);
                return run is null ? Results.NotFound() : Results.Ok(run);
            }).RequireAuthorization();

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
