using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.StepPackages;
using KrakenDeploy.Server.Data.Services;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Tests for <see cref="StepPackageResolver"/> (Phase D-6). Two surfaces:
/// the internal semver comparator (pure unit) and the step-type lookup
/// against a real Postgres database (via <see cref="PostgresFixture"/>).
/// </summary>
public sealed class StepPackageResolverUnitTests
{
    [Theory]
    [InlineData("1.0.0", "2.0.0", "2.0.0")]
    [InlineData("1.0.0", "1.0.1", "1.0.1")]
    [InlineData("1.0.0", "1.1.0", "1.1.0")]
    [InlineData("2.0.0-rc.1", "2.0.0", "2.0.0")]    // released MMP beats pre-release
    [InlineData("2.0.0-alpha", "2.0.0-beta", "2.0.0-beta")] // lex ordering on pre-release suffix
    [InlineData("10.0.0", "9.9.9", "10.0.0")]       // numeric, not lexical, on the MMP cores
    public void PickHighestSemver_picks_the_higher_of_two(string a, string b, string expected)
    {
        var picked = StepPackageResolver.PickHighestSemver(new[] { a, b });
        picked.Should().Be(expected);
    }

    [Fact]
    public void PickHighestSemver_returns_null_for_empty_input()
        => StepPackageResolver.PickHighestSemver(Array.Empty<string>()).Should().BeNull();

    [Fact]
    public void OrderByHighestSemver_returns_versions_highest_first()
    {
        // Used by the editor's version dropdown (D-7) — index 0 must be
        // "latest installed" so the "Update available" badge can compare
        // a pinned version against _availableVersions[0] in O(1).
        var versions = new[] { "1.0.0", "2.0.0-rc.1", "1.10.0", "2.0.0", "1.2.0" };

        var ordered = StepPackageResolver.OrderByHighestSemver(versions);

        ordered.Should().Equal(["2.0.0", "2.0.0-rc.1", "1.10.0", "1.2.0", "1.0.0"]);
    }

    [Fact]
    public void OrderByHighestSemver_drops_blank_entries()
    {
        var ordered = StepPackageResolver.OrderByHighestSemver(
            ["1.0.0", "", "   ", "2.0.0", null!]);

        ordered.Should().Equal(["2.0.0", "1.0.0"]);
    }

    [Fact]
    public void PickHighestSemver_picks_correctly_across_many_versions()
    {
        // SemVer: pre-release identifiers only push a version below the
        // *same* MMP without suffix — they do NOT push it below a lower MMP.
        // So 2.0.0-rc.1 still beats 1.99.0.
        var versions = new[]
        {
            "1.0.0", "1.2.3", "0.9.0", "1.10.0", "1.2.0", "2.0.0-rc.1", "1.99.0",
        };
        StepPackageResolver.PickHighestSemver(versions).Should().Be("2.0.0-rc.1",
            "pre-releases of a higher MMP still rank above any lower MMP");
    }

    [Fact]
    public void PickHighestSemver_prefers_released_over_pre_release_of_same_MMP()
    {
        StepPackageResolver.PickHighestSemver(["2.0.0", "2.0.0-rc.1", "2.0.0-rc.2"])
            .Should().Be("2.0.0",
                "a released 2.0.0 beats any 2.0.0-pre");
    }
}

[Collection("Postgres")]
public sealed class StepPackageResolverDbTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ResolveLatestForStepTypeAsync_returns_null_when_no_package_claims_the_type()
    {
        var resolver = new StepPackageResolver(postgres);
        var pin = await resolver.ResolveLatestForStepTypeAsync("Kraken.NonExistent");
        pin.Should().BeNull();
    }

    [Fact]
    public async Task ResolveLatestForStepTypeAsync_picks_the_highest_semver_for_the_claimed_step_type()
    {
        var pkg   = UniquePkgName();
        var other = UniquePkgName();

        await using (var db = postgres.CreateContext())
        {
            db.StepPackages.AddRange(
                NewPackage(pkg,   "1.0.0",  pkg),
                NewPackage(pkg,   "1.10.0", pkg),
                NewPackage(pkg,   "1.2.0",  pkg),
                NewPackage(other, "9.9.9",  other + ".other"));
            await db.SaveChangesAsync();
        }

        var resolver = new StepPackageResolver(postgres);
        var pin = await resolver.ResolveLatestForStepTypeAsync(pkg);

        pin.Should().NotBeNull();
        pin!.Name.Should().Be(pkg);
        pin.Version.Should().Be("1.10.0",
            "1.10.0 > 1.2.0 numerically; semver beats lex order");
    }

    [Fact]
    public async Task ResolveLatestForStepTypeAsync_matches_case_insensitively_on_step_type()
    {
        var pkg = UniquePkgName();
        await using (var db = postgres.CreateContext())
        {
            db.StepPackages.Add(NewPackage(pkg, "1.5.0", pkg.ToLowerInvariant()));
            await db.SaveChangesAsync();
        }

        // Denormalised StepTypes column stores lower-case; the resolver
        // lower-cases the needle so the surface API is case-tolerant.
        var resolver = new StepPackageResolver(postgres);
        var pin = await resolver.ResolveLatestForStepTypeAsync(pkg.ToUpperInvariant());
        pin.Should().NotBeNull();
        pin!.Version.Should().Be("1.5.0");
    }

    [Fact]
    public async Task ResolveLatestForStepTypeAsync_handles_packages_claiming_multiple_step_types()
    {
        var pkg   = UniquePkgName();
        var typeA = pkg + ".a";
        var typeB = pkg + ".b";
        await using (var db = postgres.CreateContext())
        {
            // A package whose denormalised list contains both step types —
            // both must resolve to this same install.
            db.StepPackages.Add(NewPackage(pkg, "2.3.0", typeA + "," + typeB));
            await db.SaveChangesAsync();
        }

        var resolver = new StepPackageResolver(postgres);
        var a = await resolver.ResolveLatestForStepTypeAsync(typeA);
        var b = await resolver.ResolveLatestForStepTypeAsync(typeB);
        a.Should().NotBeNull(); b.Should().NotBeNull();
        a!.Version.Should().Be("2.3.0");
        b!.Version.Should().Be("2.3.0");
    }

    private static string UniquePkgName()
        => "kraken.sample." + Guid.NewGuid().ToString("N")[..8];

    private static StepPackage NewPackage(string name, string version, string stepTypes)
        => new()
        {
            Name         = name,
            Version      = version,
            Sha256       = new string('a', 64),
            ManifestJson = "{}",
            UiSchemaJson = null,
            Source       = StepPackageSource.LocalUpload,
            StepTypes    = stepTypes,
        };
}
