using KrakenDeploy.Server.Commands;
using KrakenDeploy.Server.Components;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
