using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Licensing;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Integration tests for <see cref="UserService.CreateServiceAccountAsync"/>.
/// Identity needs the full UserManager pipeline (password hasher, validators,
/// stores) so we spin up a minimal DI tree against the Postgres fixture
/// rather than newing up UserManager directly.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class UserServiceServiceAccountTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        // Wipe identity rows between tests. Identity tables are not
        // ISpaceScoped so a plain ExecuteDeleteAsync clears them.
        await using var db = postgres.CreateContext();
        await db.Users.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Creates_service_account_with_Kind_set()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();

        var sa = await svc.CreateServiceAccountAsync("deployment-bot");

        sa.Kind.Should().Be(UserKind.ServiceAccount);
        sa.UserName.Should().Be("svc-deployment-bot",
            "username derives from slugified display name with svc- prefix; " +
            "audit rows and team lists need a distinguishable label");
        sa.Email.Should().Be("svc-deployment-bot@kraken.local",
            "synthetic email uses the non-deliverable .kraken.local TLD so " +
            "nobody accidentally emails the bot");
        sa.EmailConfirmed.Should().BeTrue();
    }

    [Fact]
    public async Task Service_account_has_no_local_password()
    {
        // The contract: service accounts authenticate ONLY via API keys.
        // Pin that no password is set so the password sign-in flow refuses
        // by default (CheckPasswordSignInAsync returns failure when the
        // password hash is null).
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();
        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();

        var sa = await svc.CreateServiceAccountAsync("token-issuer");

        (await userManager.HasPasswordAsync(sa)).Should().BeFalse(
            "service accounts must not carry a local password — that's " +
            "the boundary that keeps interactive sign-in disabled");
    }

    [Fact]
    public async Task Slugifies_display_name_into_username()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();

        var sa = await svc.CreateServiceAccountAsync("Deployment Bot #1");

        sa.UserName.Should().Be("svc-deployment-bot-1",
            "ProjectService.Slugify produces lowercase + dash-separated; " +
            "the bot is therefore findable by a stable URL-safe name");
    }

    [Fact]
    public async Task Refuses_duplicate_display_name()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();

        await svc.CreateServiceAccountAsync("backup-runner");

        var act = async () => await svc.CreateServiceAccountAsync("backup-runner");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task Refuses_when_license_user_cap_reached()
    {
        // Service accounts count against MaxUsers — they're identity rows
        // just like humans, and a license-evading "spawn 1000 bots"
        // workflow shouldn't get past the cap.
        await using var sp = BuildServiceProvider(
            licenseGate: new FakeLicenseGate(
                userRefusal: "User limit reached (5/5). Upgrade your license."));
        var svc = sp.GetRequiredService<UserService>();

        var act = async () => await svc.CreateServiceAccountAsync("over-the-cap");

        await act.Should().ThrowAsync<LicenseLimitException>()
            .WithMessage("*User limit reached*");
    }

    [Fact]
    public async Task Refuses_empty_or_whitespace_display_name()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await svc.CreateServiceAccountAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await svc.CreateServiceAccountAsync("   "));
    }

    [Fact]
    public async Task Refuses_name_that_slugifies_to_empty()
    {
        // Non-alphanumeric input slugifies to "" — the implementation must
        // reject this before the synthetic username becomes "svc-".
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();

        var act = async () => await svc.CreateServiceAccountAsync("!!!  @@@");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*alphanumeric*");
    }

    [Fact]
    public async Task Human_invite_and_service_account_share_user_count()
    {
        // Pin that adding humans + service accounts together respects the
        // cap as a combined total. A separate counter would let an operator
        // double the effective cap (5 humans + 5 bots when license says 5).
        var gate = new CountingFakeGate(limit: 2);
        await using var sp = BuildServiceProvider(licenseGate: gate);
        var svc = sp.GetRequiredService<UserService>();

        await svc.InviteAsync("alice@laus.hr");
        await svc.CreateServiceAccountAsync("bot");

        var act = async () => await svc.InviteAsync("bob@laus.hr");

        await act.Should().ThrowAsync<LicenseLimitException>(
            "two identities already exist (one human + one service); " +
            "adding a third must hit the cap");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal DI tree with the full Identity stack pointed at the
    /// Postgres fixture. The fixture's connection string is used so all
    /// services share the same migrated schema.
    /// </summary>
    private ServiceProvider BuildServiceProvider(ILicenseGate? licenseGate = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);

        // The postgres fixture already implements IDbContextFactory<KrakenDbContext>
        // and wires the DefaultSpaceContext + audit interceptor. Reusing it
        // here avoids duplicating the EF options + having to provide
        // ISpaceContext separately.
        services.AddSingleton<IDbContextFactory<KrakenDbContext>>(postgres);

        // Identity's EF user-store needs a scoped DbContext.
        services.AddScoped(_ => postgres.CreateContext());

        services.AddIdentityCore<ApplicationUser>(opt =>
        {
            opt.Password.RequireDigit           = false;
            opt.Password.RequireLowercase       = false;
            opt.Password.RequireUppercase       = false;
            opt.Password.RequireNonAlphanumeric = false;
            opt.Password.RequiredLength         = 6;
        })
        .AddEntityFrameworkStores<KrakenDbContext>();

        services.AddSingleton(licenseGate ?? FakeLicenseGate.Unlimited);
        services.AddScoped<UserService>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// License gate that refuses once <c>currentUserCount &gt;= limit</c>.
    /// Mirrors the production gate's "&gt;= max" semantics so the combined-
    /// count test exercises the real boundary, not an off-by-one fake.
    /// </summary>
    private sealed class CountingFakeGate(int limit) : ILicenseGate
    {
        public string? CheckTargetCreate(int currentTargetCount) => null;
        public string? CheckUserCreate(int currentUserCount) =>
            currentUserCount >= limit
                ? $"User limit reached ({currentUserCount}/{limit}). Upgrade your license."
                : null;
    }
}
