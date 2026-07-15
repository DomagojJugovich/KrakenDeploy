using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Licensing;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Data.Identity;
using KrakenDeploy.Server.Data.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// A7 / T1-13 — the session-revocation TRIGGERS: disabling an account and
/// resetting a password must bump the security stamp (so the wired
/// SecurityStampValidator + revalidating circuit provider reject the stale
/// principal) and, for disable, set the persistent IsDisabled gate. The
/// validator/provider wiring itself lives in Program.cs and is exercised by a
/// host boot; here we pin the DB-facing behavior.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class UserServiceRevocationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.Users.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SetDisabledAsync_sets_flag_and_bumps_security_stamp()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();
        var um = sp.GetRequiredService<UserManager<ApplicationUser>>();

        var (user, _) = await svc.InviteAsync("alice@laus.hr");
        var stampBefore = (await um.FindByIdAsync(user.Id.ToString()))!.SecurityStamp;

        var ok = await svc.SetDisabledAsync(user.Id, true);

        ok.Should().BeTrue();
        var reloaded = (await um.FindByIdAsync(user.Id.ToString()))!;
        reloaded.IsDisabled.Should().BeTrue();
        reloaded.SecurityStamp.Should().NotBe(stampBefore,
            "disabling must bump the security stamp so live sessions/circuits are revoked");
    }

    [Fact]
    public async Task SetDisabledAsync_reenable_clears_flag()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();
        var um = sp.GetRequiredService<UserManager<ApplicationUser>>();

        var (user, _) = await svc.InviteAsync("bob@laus.hr");
        await svc.SetDisabledAsync(user.Id, true);

        var ok = await svc.SetDisabledAsync(user.Id, false);

        ok.Should().BeTrue();
        (await um.FindByIdAsync(user.Id.ToString()))!.IsDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task SetDisabledAsync_is_idempotent_and_leaves_stamp_when_already_in_state()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();
        var um = sp.GetRequiredService<UserManager<ApplicationUser>>();

        var (user, _) = await svc.InviteAsync("carol@laus.hr");
        var stampBefore = (await um.FindByIdAsync(user.Id.ToString()))!.SecurityStamp;

        // Already enabled -> disabling to the same state is a no-op.
        var ok = await svc.SetDisabledAsync(user.Id, false);

        ok.Should().BeTrue();
        (await um.FindByIdAsync(user.Id.ToString()))!.SecurityStamp.Should().Be(stampBefore,
            "a no-op state change must not churn the security stamp");
    }

    [Fact]
    public async Task SetDisabledAsync_returns_false_for_missing_user()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();

        (await svc.SetDisabledAsync(Guid.NewGuid(), true)).Should().BeFalse();
    }

    [Fact]
    public async Task ResetPasswordAsync_replaces_password_and_bumps_stamp()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();
        var um = sp.GetRequiredService<UserManager<ApplicationUser>>();

        var (user, originalTemp) = await svc.InviteAsync("dave@laus.hr");
        var stampBefore = (await um.FindByIdAsync(user.Id.ToString()))!.SecurityStamp;

        var newTemp = await svc.ResetPasswordAsync(user.Id);

        newTemp.Should().NotBeNullOrWhiteSpace();
        newTemp.Should().NotBe(originalTemp);

        var reloaded = (await um.FindByIdAsync(user.Id.ToString()))!;
        reloaded.SecurityStamp.Should().NotBe(stampBefore,
            "resetting the password must bump the stamp so other sessions are revoked");
        (await um.CheckPasswordAsync(reloaded, originalTemp)).Should().BeFalse(
            "the old password must stop working");
        (await um.CheckPasswordAsync(reloaded, newTemp!)).Should().BeTrue(
            "the new temporary password must work");
    }

    [Fact]
    public async Task ResetPasswordAsync_refuses_service_account()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();

        var sa = await svc.CreateServiceAccountAsync("deploy-bot");

        var act = async () => await svc.ResetPasswordAsync(sa.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*password*");
    }

    [Fact]
    public async Task ResetPasswordAsync_returns_null_for_missing_user()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();

        (await svc.ResetPasswordAsync(Guid.NewGuid())).Should().BeNull();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IDbContextFactory<KrakenDbContext>>(postgres);
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
        services.AddSingleton<ILicenseGate>(FakeLicenseGate.Unlimited);
        services.AddScoped<UserService>();
        return services.BuildServiceProvider();
    }
}
