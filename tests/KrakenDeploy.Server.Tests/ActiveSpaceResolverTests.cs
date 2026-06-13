using FluentAssertions;
using KrakenDeploy.Server.Spaces;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Tests for the fail-closed active-Space resolution shared by the request
/// middleware and the interactive-circuit boundary. The security-critical
/// properties: a candidate Space is honoured ONLY when it is in the accessible
/// set, and a user with no accessible Space falls back to a no-match sentinel
/// (never blindly the Default Space).
/// </summary>
public sealed class ActiveSpaceResolverTests
{
    private static readonly Guid Default = new("00000000-0000-0000-0000-00000000d543");

    private static HashSet<Guid> Set(params Guid[] ids) => new(ids);

    [Fact]
    public void Honours_candidate_when_accessible()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        ActiveSpaceResolver.Resolve(a, Set(a, b, Default), Default).Should().Be(a);
    }

    [Fact]
    public void Discards_candidate_when_not_accessible_and_prefers_default()
    {
        var notMine = Guid.NewGuid();
        ActiveSpaceResolver.Resolve(notMine, Set(Default), Default)
            .Should().Be(Default, "an inaccessible candidate must never be honoured");
    }

    [Fact]
    public void Null_candidate_prefers_default_when_accessible()
    {
        var other = Guid.NewGuid();
        ActiveSpaceResolver.Resolve(null, Set(Default, other), Default).Should().Be(Default);
    }

    [Fact]
    public void Falls_back_to_lowest_accessible_when_default_not_accessible()
    {
        // Member of two non-Default Spaces only — must land on one of them, not Default.
        var s1 = new Guid("11111111-1111-1111-1111-111111111111");
        var s2 = new Guid("22222222-2222-2222-2222-222222222222");
        ActiveSpaceResolver.Resolve(null, Set(s2, s1), Default).Should().Be(s1);
        ActiveSpaceResolver.Resolve(Default, Set(s2, s1), Default)
            .Should().Be(s1, "Default is not accessible so the inaccessible candidate is discarded too");
    }

    [Fact]
    public void Fails_closed_when_no_accessible_space()
    {
        ActiveSpaceResolver.Resolve(Guid.NewGuid(), Set(), Default)
            .Should().Be(Guid.Empty, "a user with no accessible Space must not leak the Default Space");
        ActiveSpaceResolver.Resolve(Default, Set(), Default).Should().Be(Guid.Empty);
        ActiveSpaceResolver.Resolve(null, Set(), Default).Should().Be(Guid.Empty);
    }
}
