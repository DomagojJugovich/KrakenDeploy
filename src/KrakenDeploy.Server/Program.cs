using System.Text;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Commands;
using KrakenDeploy.Server.Components;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Services;
using KrakenDeploy.Server.Services;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Radzen;

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

        return await RunWebAsync(args).ConfigureAwait(false);
    }

    private static async Task<int> RunWebAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var connectionString = builder.Configuration.GetConnectionString("KrakenDb")
            ?? throw new InvalidOperationException(
                "Connection string 'KrakenDb' is not configured. " +
                "Set ConnectionStrings:KrakenDb in appsettings.{Environment}.json or via user-secrets.");

        builder.Services.AddKrakenDeployData(connectionString);
        builder.Services.AddKrakenDeployIdentityCore();

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

        builder.Services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 1_048_576; // 1 MiB — control plane only
        });

        builder.Services.AddSingleton<IAgentConnectionRegistry, InMemoryAgentConnectionRegistry>();
        builder.Services.AddSingleton<AgentJwtService>();

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddRadzenComponents();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
            await db.Database.MigrateAsync().ConfigureAwait(false);
            await PrintFirstRunHintIfNoUsersAsync(scope.ServiceProvider, app.Logger).ConfigureAwait(false);
        }
        else
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseHttpsRedirection();
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

        app.MapGet("/healthz", async (KrakenDbContext db, CancellationToken ct) =>
        {
            var canConnect = await db.Database.CanConnectAsync(ct).ConfigureAwait(false);
            if (!canConnect)
            {
                return Results.Json(new { status = "unhealthy", reason = "database unreachable" }, statusCode: 503);
            }
            var targets = await db.DeploymentTargets.CountAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { status = "ok", targets });
        }).AllowAnonymous();

        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task PrintFirstRunHintIfNoUsersAsync(IServiceProvider services, ILogger logger)
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
