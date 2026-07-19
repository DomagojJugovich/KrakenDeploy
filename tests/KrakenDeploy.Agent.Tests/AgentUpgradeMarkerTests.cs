using FluentAssertions;
using KrakenDeploy.Agent.Services;

namespace KrakenDeploy.Agent.Tests;

/// <summary>C6 — round-trip and resilience tests for the self-upgrade marker.</summary>
public sealed class AgentUpgradeMarkerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"kraken-marker-{Guid.NewGuid():N}");

    private string MarkerPath => Path.Combine(_dir, "updates", "upgrade-pending.json");

    public AgentUpgradeMarkerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* non-fatal */ }
    }

    private static AgentUpgradeMarker Sample() => new()
    {
        FromVersion             = "1.2.3",
        ToVersion               = "1.2.4",
        InstallDir              = @"C:\Program Files\KrakenAgent",
        BackupDir               = @"C:\Program Files\KrakenAgent.backup",
        WrittenUtc              = new DateTimeOffset(2026, 7, 19, 2, 15, 0, TimeSpan.Zero),
        HealthTimeoutSeconds    = 180,
        ExpectedContractVersion = 1,
        AttemptsUsed            = 2,
    };

    [Fact]
    public void Save_then_TryLoad_round_trips_all_fields()
    {
        var marker = Sample();
        AgentUpgradeMarker.Save(MarkerPath, marker);

        var loaded = AgentUpgradeMarker.TryLoad(MarkerPath);

        loaded.Should().BeEquivalentTo(marker);
    }

    [Fact]
    public void TryLoad_returns_null_when_missing()
        => AgentUpgradeMarker.TryLoad(MarkerPath).Should().BeNull();

    [Fact]
    public void TryLoad_returns_null_for_corrupt_marker()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
        File.WriteAllText(MarkerPath, "{ this is not valid json");

        AgentUpgradeMarker.TryLoad(MarkerPath).Should().BeNull();
    }

    [Fact]
    public void Delete_removes_the_marker()
    {
        AgentUpgradeMarker.Save(MarkerPath, Sample());
        File.Exists(MarkerPath).Should().BeTrue();

        AgentUpgradeMarker.Delete(MarkerPath);

        File.Exists(MarkerPath).Should().BeFalse();
    }

    [Fact]
    public void Save_is_atomic_and_leaves_no_temp_file()
    {
        AgentUpgradeMarker.Save(MarkerPath, Sample());

        File.Exists(MarkerPath).Should().BeTrue();
        File.Exists(MarkerPath + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void Save_overwrites_an_existing_marker_and_persists_incremented_attempts()
    {
        var marker = Sample();
        AgentUpgradeMarker.Save(MarkerPath, marker);

        // Simulate the per-restart attempt bump.
        AgentUpgradeMarker.Save(MarkerPath, marker with { AttemptsUsed = marker.AttemptsUsed + 1 });

        var loaded = AgentUpgradeMarker.TryLoad(MarkerPath);
        loaded!.AttemptsUsed.Should().Be(marker.AttemptsUsed + 1);
        File.Exists(MarkerPath + ".tmp").Should().BeFalse();
    }
}
