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
/// WP5 item 4 — user profile edit. <see cref="UserService.UpdateProfileAsync"/>
/// sets the optional display name and (for humans) changes the email through the
/// UserManager pipeline, keeping the username (the sign-in identity, equal to the
/// email for humans) in step and re-normalizing the address. Service accounts keep
/// their synthetic email — only the display name is editable.
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class UserServiceProfileTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.Users.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpdateProfileAsync_sets_display_name()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();
        var um = sp.GetRequiredService<UserManager<ApplicationUser>>();

        var (user, _) = await svc.InviteAsync("alice@laus.hr");

        var ok = await svc.UpdateProfileAsync(user.Id, "Alice Horvat", email: null);

        ok.Should().BeTrue();
        (await um.FindByIdAsync(user.Id.ToString()))!.DisplayName.Should().Be("Alice Horvat");
    }

    [Fact]
    public async Task UpdateProfileAsync_blank_display_name_clears_it()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();
        var um = sp.GetRequiredService<UserManager<ApplicationUser>>();

        var (user, _) = await svc.InviteAsync("bob@laus.hr");
        await svc.UpdateProfileAsync(user.Id, "Bob", email: null);

        await svc.UpdateProfileAsync(user.Id, "   ", email: null);

        (await um.FindByIdAsync(user.Id.ToString()))!.DisplayName.Should().BeNull(
            "a blank display name is stored as null so surfaces fall back to the email");
    }

    [Fact]
    public async Task UpdateProfileAsync_changes_email_and_keeps_username_in_step()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();
        var um = sp.GetRequiredService<UserManager<ApplicationUser>>();

        var (user, _) = await svc.InviteAsync("carol@laus.hr");

        var ok = await svc.UpdateProfileAsync(user.Id, displayName: null, email: "carol.new@laus.hr");

        ok.Should().BeTrue();
        var reloaded = (await um.FindByIdAsync(user.Id.ToString()))!;
        reloaded.Email.Should().Be("carol.new@laus.hr");
        reloaded.UserName.Should().Be("carol.new@laus.hr",
            "humans sign in with username == email, so the username must track the new address");
        // The new address must be resolvable through the normalized-email lookup.
        (await um.FindByEmailAsync("carol.new@laus.hr"))!.Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task UpdateProfileAsync_refuses_a_duplicate_email()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();

        await svc.InviteAsync("dave@laus.hr");
        var (erin, _) = await svc.InviteAsync("erin@laus.hr");

        var act = () => svc.UpdateProfileAsync(erin.Id, displayName: null, email: "dave@laus.hr");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task UpdateProfileAsync_service_account_keeps_synthetic_email()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();
        var um = sp.GetRequiredService<UserManager<ApplicationUser>>();

        var sa = await svc.CreateServiceAccountAsync("deploy-bot");
        var emailBefore = sa.Email;

        // Passing an email for a service account is ignored — only the display name sticks.
        var ok = await svc.UpdateProfileAsync(sa.Id, "Deploy Bot", email: "hijack@laus.hr");

        ok.Should().BeTrue();
        var reloaded = (await um.FindByIdAsync(sa.Id.ToString()))!;
        reloaded.DisplayName.Should().Be("Deploy Bot");
        reloaded.Email.Should().Be(emailBefore,
            "a service account's synthetic @kraken.local email must not be editable");
        reloaded.UserName.Should().Be("svc-deploy-bot");
    }

    [Fact]
    public async Task UpdateProfileAsync_returns_false_for_missing_user()
    {
        await using var sp = BuildServiceProvider();
        var svc = sp.GetRequiredService<UserService>();

        (await svc.UpdateProfileAsync(Guid.NewGuid(), "X", "x@laus.hr")).Should().BeFalse();
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
